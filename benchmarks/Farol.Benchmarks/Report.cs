using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Farol.Benchmarks;

/// <summary>The benchmark's results as Markdown (for people and the CI job summary) and JSON (for comparisons over time).</summary>
internal static class Report
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string Markdown(Corpus corpus, CorpusSize size, string farolVersion, FarolServer server, BenchmarkRun run)
    {
        var text = new StringBuilder();
        text.AppendLine(Invariant($"## Farol {farolVersion} on {corpus.Name}"));
        text.AppendLine();
        text.AppendLine(Invariant($"Commit `{corpus.Commit[..12]}` of {corpus.Repository}: {size.Lines:N0} lines of C# and VB in {size.Files:N0} files. {Environment.ProcessorCount} logical processors, {RuntimeInformation.OSDescription}."));
        text.AppendLine();
        text.AppendLine("| Measure | Calls | p50 | p95 | Max | Target | Result |");
        text.AppendLine("|---|---:|---:|---:|---:|---|---|");
        foreach (var series in new[] { run.SymbolQueries, run.FindReferences, run.Check })
        {
            text.AppendLine(Invariant($"| {series.Name} | {series.Samples.Count} | {Ms(series.P50)} | {Ms(series.P95)} | {Ms(series.Max)} | p95 under {Ms(series.TargetMilliseconds)} | {Verdict(series.Passed)} |"));
        }

        text.AppendLine(Invariant($"| Peak memory of the server | | | | {Gb(run.PeakWorkingSet)} | under {Gb(BenchmarkRun.MemoryTarget)} | {Verdict(run.PeakWorkingSet < BenchmarkRun.MemoryTarget)} |"));
        text.AppendLine(Invariant($"| tools/list after the process started | | | | {Ms(server.ToolsListed.TotalMilliseconds)} | before the load ends | {Verdict(server.ToolsListed < run.Load)} |"));
        text.AppendLine(Invariant($"| Workspace load | | | | {run.Load.TotalSeconds:N0} s | | |"));
        text.AppendLine(Invariant($"| Background warm-up after the load (the calls are timed after it) | | | | {run.WarmUp.TotalSeconds:N0} s | | |"));
        text.AppendLine();

        foreach (var series in new[] { run.SymbolQueries, run.FindReferences, run.Check })
        {
            text.AppendLine(Invariant($"Slowest {char.ToLowerInvariant(series.Name[0])}{series.Name[1..]}: ")
                + string.Join("; ", series.Slowest(3).Select(s => Invariant($"{s.Label} ({Ms(s.Milliseconds)})"))) + ".");
            text.AppendLine();
        }

        if (run.Failures.Count > 0)
        {
            text.AppendLine("**Failed calls** (the targets cannot pass while a call fails):");
            text.AppendLine();
            foreach (var failure in run.Failures)
            {
                text.Append("- ").AppendLine(failure);
            }

            text.AppendLine();
        }

        text.AppendLine(run.Passed ? "**AC-35: every target met.**" : "**AC-35: not met.**");
        return text.ToString();
    }

    public static string Json(Corpus corpus, CorpusSize size, string farolVersion, FarolServer server, BenchmarkRun run)
    {
        var document = new JsonObject
        {
            ["corpus"] = corpus.Name,
            ["repository"] = corpus.Repository,
            ["commit"] = corpus.Commit,
            ["lines"] = size.Lines,
            ["files"] = size.Files,
            ["farol"] = farolVersion,
            ["date"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["processors"] = Environment.ProcessorCount,
            ["os"] = RuntimeInformation.OSDescription,
            ["toolsListedMilliseconds"] = Math.Round(server.ToolsListed.TotalMilliseconds),
            ["loadSeconds"] = Math.Round(run.Load.TotalSeconds, 1),
            ["warmUpSeconds"] = Math.Round(run.WarmUp.TotalSeconds, 1),
            ["peakWorkingSetBytes"] = run.PeakWorkingSet,
            ["passed"] = run.Passed,
            ["failures"] = new JsonArray([.. run.Failures.Select(f => (JsonNode)f)]),
            ["series"] = new JsonArray([.. new[] { run.SymbolQueries, run.FindReferences, run.Check }.Select(s => (JsonNode)new JsonObject
            {
                ["name"] = s.Name,
                ["targetMilliseconds"] = s.TargetMilliseconds,
                ["p50"] = Math.Round(s.P50, 1),
                ["p95"] = Math.Round(s.P95, 1),
                ["max"] = Math.Round(s.Max, 1),
                ["passed"] = s.Passed,
                ["samples"] = new JsonArray([.. s.Samples.Select(x => (JsonNode)new JsonObject { ["label"] = x.Label, ["milliseconds"] = Math.Round(x.Milliseconds, 1) })]),
            })]),
        };
        return document.ToJsonString(Indented);
    }

    /// <summary>
    /// GitHub Actions workflow commands: a notice with the results and an error per failed call or missed target. Unlike
    /// the job log and summary, a check run's annotations can be read without signing in.
    /// </summary>
    public static IEnumerable<string> Annotations(Corpus corpus, FarolServer server, BenchmarkRun run)
    {
        var series = new[] { run.SymbolQueries, run.FindReferences, run.Check };
        var results = string.Join("; ", series.Select(s => Invariant($"{s.Name} p95 {Ms(s.P95)} (target {Ms(s.TargetMilliseconds)})")))
            + Invariant($"; peak memory {Gb(run.PeakWorkingSet)}; load {run.Load.TotalSeconds:N0} s; warm-up {run.WarmUp.TotalSeconds:N0} s; tools/list {Ms(server.ToolsListed.TotalMilliseconds)}; {Environment.ProcessorCount} logical processors.");
        yield return Command("notice", Invariant($"AC-35 on {corpus.Name}: {(run.Passed ? "met" : "not met")}"), results);

        foreach (var missed in series.Where(s => s.Samples.Count > 0 && !s.Passed))
        {
            var slowest = string.Join("; ", missed.Slowest(5).Select(s => Invariant($"{s.Label} ({Ms(s.Milliseconds)})")));
            yield return Command("error", Invariant($"Target missed on {corpus.Name}"), Invariant($"{missed.Name}: p95 {Ms(missed.P95)}, target {Ms(missed.TargetMilliseconds)}. Slowest: {slowest}."));
        }

        if (run.PeakWorkingSet >= BenchmarkRun.MemoryTarget)
        {
            yield return Command("error", Invariant($"Target missed on {corpus.Name}"), Invariant($"Peak memory {Gb(run.PeakWorkingSet)}, target {Gb(BenchmarkRun.MemoryTarget)}."));
        }

        foreach (var failure in run.Failures)
        {
            yield return Command("error", Invariant($"Failed call on {corpus.Name}"), failure);
        }
    }

    private static string Command(string level, string title, string message) =>
        $"::{level} title={Escape(title).Replace(":", "%3A", StringComparison.Ordinal).Replace(",", "%2C", StringComparison.Ordinal)}::{Escape(message)}";

    private static string Escape(string text) => text
        .Replace("%", "%25", StringComparison.Ordinal)
        .Replace("\r", "%0D", StringComparison.Ordinal)
        .Replace("\n", "%0A", StringComparison.Ordinal);

    private static string Ms(double milliseconds) => milliseconds >= 1000
        ? Invariant($"{milliseconds / 1000:N2} s")
        : Invariant($"{milliseconds:N0} ms");

    private static string Gb(long bytes) => Invariant($"{bytes / (1024.0 * 1024 * 1024):N2} GB");

    private static string Verdict(bool passed) => passed ? "pass" : "**fail**";

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
