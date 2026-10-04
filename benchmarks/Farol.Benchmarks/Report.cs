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

    private static string Ms(double milliseconds) => milliseconds >= 1000
        ? Invariant($"{milliseconds / 1000:N2} s")
        : Invariant($"{milliseconds:N0} ms");

    private static string Gb(long bytes) => Invariant($"{bytes / (1024.0 * 1024 * 1024):N2} GB");

    private static string Verdict(bool passed) => passed ? "pass" : "**fail**";

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
