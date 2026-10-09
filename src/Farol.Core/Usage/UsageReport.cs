using System.Globalization;
using System.Text;
using System.Text.Json;
using static System.FormattableString;

namespace Farol.Core.Usage;

/// <summary>
/// Sums the usage log up for a period: the days Farol was used, the sessions and clients, each tool's calls, errors
/// and durations, the errors by code and the loads per workspace hash. It only reads; nothing leaves the machine.
/// </summary>
public static class UsageReport
{
    public const int DefaultDays = 14;

    public const int MaxDays = 366;

    public static string Build(string directory, int days, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        days = Math.Clamp(days, 1, MaxDays);
        var last = DateOnly.FromDateTime(now.DateTime);
        var first = last.AddDays(1 - days);
        var (events, unreadable) = Read(directory, first, last);

        var text = new StringBuilder();
        text.AppendLine(Invariant($"Farol usage report: {first:yyyy-MM-dd} to {last:yyyy-MM-dd} ({days} days), from {Path.GetFullPath(directory)}"));
        text.AppendLine("It stays on this machine unless you share it.");
        text.AppendLine();
        if (events.Count == 0)
        {
            text.AppendLine("No usage recorded in this period. Farol keeps the log when it runs with --usage-log or Farol__UsageLog=true.");
        }
        else
        {
            WriteSummary(text, events);
        }

        if (unreadable > 0)
        {
            text.AppendLine(Invariant($"unreadable lines skipped: {unreadable}"));
        }

        return text.ToString().TrimEnd();
    }

    private static void WriteSummary(StringBuilder text, List<UsageEvent> events)
    {
        var calls = events.Where(e => e.Kind == UsageEventKind.Call).ToList();
        var days = calls.Select(e => DateOnly.FromDateTime(e.Timestamp.DateTime)).Distinct().Order().ToList();
        text.AppendLine(days.Count == 0
            ? "days with use: 0"
            : Invariant($"days with use: {days.Count} ({string.Join(", ", days.Select(d => d.ToString("MM-dd", CultureInfo.InvariantCulture)))})"));

        var sessions = events.Select(e => e.Session).OfType<string>().Distinct(StringComparer.Ordinal).Count();
        var versions = events.Where(e => e.Kind == UsageEventKind.Start).Select(e => e.Farol ?? "unknown").ToList();
        text.AppendLine(versions.Count == 0 ? Invariant($"sessions: {sessions}") : Invariant($"sessions: {sessions}, Farol {Counts(versions)}"));

        var clients = events.Where(e => e.Kind == UsageEventKind.Session).Select(ClientName).ToList();
        if (clients.Count > 0)
        {
            text.AppendLine(Invariant($"clients: {Counts(clients)}"));
        }

        var failed = calls.Where(e => e.Outcome == UsageOutcome.Error).ToList();
        var cancelled = calls.Count(e => e.Outcome == UsageOutcome.Cancelled);
        var cut = calls.Count(e => e.Cut == true);
        text.AppendLine(Invariant($"tool calls: {calls.Count}, errors: {failed.Count}{Percent(failed.Count, calls.Count)}, cancelled: {cancelled}, cut by the token budget: {cut}"));
        if (calls.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(Invariant($"{"tool",-24}{"calls",6}{"errors",8}{"p50",10}{"p95",10}"));
            foreach (var tool in Ranked(calls.GroupBy(e => e.Tool ?? "unknown", StringComparer.Ordinal)))
            {
                var durations = tool.Select(e => e.Ms ?? 0).Order().ToList();
                var errors = tool.Count(e => e.Outcome == UsageOutcome.Error);
                text.AppendLine(Invariant($"{tool.Key,-24}{tool.Count(),6}{errors,8}{Duration(Percentile(durations, 50)),10}{Duration(Percentile(durations, 95)),10}"));
            }

            text.AppendLine();
        }

        text.AppendLine(failed.Count == 0
            ? "errors by code: none"
            : "errors by code: " + string.Join(", ", Ranked(failed.GroupBy(e => e.Error ?? ErrorCodes.Unexpected, StringComparer.Ordinal)).Select(ErrorGroup)));

        var loads = events.Where(e => e.Kind == UsageEventKind.Load).ToList();
        var warmups = events.Where(e => e.Kind == UsageEventKind.Warm && e.Outcome == UsageOutcome.Ok).ToList();
        text.AppendLine(loads.Count == 0 ? "workspaces: none loaded" : "workspaces, as a hash of the solution file name:");
        foreach (var workspace in Ranked(loads.GroupBy(e => e.Workspace ?? "unknown", StringComparer.Ordinal)))
        {
            text.AppendLine(WorkspaceLine(workspace, warmups.Where(e => e.Workspace == workspace.Key).Select(e => e.Ms ?? 0).Order().ToList()));
        }

        var crashes = events.Where(e => e.Kind == UsageEventKind.Crash).Select(e => e.Exception ?? "unknown").ToList();
        text.AppendLine(crashes.Count == 0 ? "crashes: none" : Invariant($"crashes: {Counts(crashes)}"));
    }

    private static string WorkspaceLine(IGrouping<string, UsageEvent> loads, List<long> warmups)
    {
        var loaded = loads.Where(e => e.Outcome == UsageOutcome.Ok).ToList();
        var line = new StringBuilder(Invariant($"- {loads.Key}: {loads.Count()} load(s), {loads.Count() - loaded.Count} failed"));
        if (loaded.Count > 0)
        {
            var durations = loaded.Select(e => e.Ms ?? 0).Order().ToList();
            line.Append(Invariant($", {loaded.Max(e => e.Projects ?? 0)} project(s), load p50 {Duration(Percentile(durations, 50))}, slowest {Duration(durations[^1])}, load errors {loaded.Max(e => e.LoadErrors ?? 0)}"));
        }

        if (warmups.Count > 0)
        {
            line.Append(Invariant($", warm-up p50 {Duration(Percentile(warmups, 50))}"));
        }

        return line.ToString();
    }

    private static string ErrorGroup(IGrouping<string, UsageEvent> errors)
    {
        var types = errors.Select(e => e.Exception).OfType<string>().ToList();
        return types.Count == 0
            ? Invariant($"{errors.Key} ({errors.Count()})")
            : Invariant($"{errors.Key} ({errors.Count()}: {Counts(types)})");
    }

    private static string ClientName(UsageEvent session)
    {
        var name = string.Join(' ', new[] { session.Client, session.ClientVersion }.Where(p => !string.IsNullOrWhiteSpace(p)));
        return name.Length == 0 ? "unknown" : name;
    }

    private static IEnumerable<IGrouping<string, UsageEvent>> Ranked(IEnumerable<IGrouping<string, UsageEvent>> groups) =>
        groups.OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal);

    private static string Counts(IEnumerable<string> values) =>
        string.Join(", ", values
            .GroupBy(v => v, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => Invariant($"{g.Key} ({g.Count()})")));

    private static string Percent(int part, int total) => total == 0 ? string.Empty : Invariant($" ({100.0 * part / total:0.#}%)");

    /// <summary>Nearest-rank percentile of sorted values.</summary>
    private static long Percentile(List<long> sorted, int percent) =>
        sorted.Count == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(percent / 100.0 * sorted.Count) - 1, 0, sorted.Count - 1)];

    private static string Duration(long ms) => ms < 1000 ? Invariant($"{ms} ms") : Invariant($"{ms / 1000.0:0.0} s");

    private static (List<UsageEvent> Events, int Unreadable) Read(string directory, DateOnly first, DateOnly last)
    {
        List<UsageEvent> events = [];
        var unreadable = 0;
        if (!Directory.Exists(directory))
        {
            return (events, unreadable);
        }

        foreach (var path in Directory.EnumerateFiles(directory, UsageLogFile.FilePattern))
        {
            foreach (var line in ReadLines(path).Where(l => l.Length > 0))
            {
                UsageEvent? usageEvent;
                try
                {
                    usageEvent = JsonSerializer.Deserialize<UsageEvent>(line, UsageEvent.Json);
                }
                catch (JsonException)
                {
                    usageEvent = null;
                }

                if (usageEvent is null)
                {
                    unreadable++;
                    continue;
                }

                var day = DateOnly.FromDateTime(usageEvent.Timestamp.DateTime);
                if (day >= first && day <= last)
                {
                    events.Add(usageEvent);
                }
            }
        }

        return (events, unreadable);
    }

    private static List<string> ReadLines(string path)
    {
        List<string> lines = [];
        try
        {
            // A running Farol may be appending to the file.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deleted or locked meanwhile: the report covers the rest.
        }

        return lines;
    }
}
