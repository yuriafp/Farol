using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Farol.Evals;

/// <summary>One run as run.json stores it.</summary>
internal sealed record RunRecord(
    string Task,
    string Job,
    string Repo,
    string Arm,
    int Run,
    string Model,
    string StartedAt,
    double DurationSeconds,
    string Exit,
    string? Subtype,
    int Turns,
    double CostUsd,
    TokenCount Tokens,
    IReadOnlyDictionary<string, int> ToolCalls,
    int FarolCalls,
    string? IsolationProblem,
    bool RateLimited,
    IReadOnlyList<CheckOutcome> Checks,
    bool Success,
    string Answer,
    string? Error)
{
    /// <summary>Runs that say nothing about Farol: contaminated isolation, a usage limit, an interrupted suite, a harness failure.</summary>
    public bool Valid => IsolationProblem is null && !RateLimited && Exit != "cancelled" && Error is null;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(RunRecord))]
internal sealed partial class RunJson : JsonSerializerContext;

/// <summary>
/// The suite's results against spec 001's exit criterion: with Farol, the success rate at least 10 points higher and
/// tokens per task at least 20% lower than grep + build (the baseline arm).
/// </summary>
internal static class Summary
{
    public const double RequiredSuccessGain = 0.10;
    public const double RequiredTokenReduction = 0.20;

    public static IReadOnlyList<RunRecord> Load(string directory) =>
        [.. Directory.EnumerateFiles(directory, "run.json", SearchOption.AllDirectories)
            .Select(f => JsonSerializer.Deserialize(File.ReadAllText(f), RunJson.Default.RunRecord)!)
            .OrderBy(r => r.Task, StringComparer.Ordinal).ThenBy(r => r.Arm, StringComparer.Ordinal).ThenBy(r => r.Run)];

    public static (string Markdown, bool Met) Write(string directory, IReadOnlyList<RunRecord> runs, string claudeVersion)
    {
        var valid = runs.Where(r => r.Valid).ToList();
        var farol = valid.Where(r => r.Arm == "farol").ToList();
        var baseline = valid.Where(r => r.Arm == "baseline").ToList();
        var gain = Rate(farol) - Rate(baseline);
        var reduction = baseline.Count == 0 || MeanTokens(baseline) == 0 ? 0 : 1 - (MeanTokens(farol) / MeanTokens(baseline));
        var met = farol.Count > 0 && baseline.Count > 0 && gain >= RequiredSuccessGain && reduction >= RequiredTokenReduction;

        var text = new StringBuilder();
        text.AppendLine("# Farol eval suite");
        text.AppendLine();
        var models = string.Join(", ", runs.Select(r => r.Model).Distinct(StringComparer.Ordinal));
        text.AppendLine(Invariant($"Model {models} · Claude Code {claudeVersion} · {runs.Select(r => r.Task).Distinct().Count()} tasks · {valid.Count} valid runs of {runs.Count} · total cost ${runs.Sum(r => r.CostUsd):N2} (list price)"));
        text.AppendLine();
        text.AppendLine("## Exit criterion (spec 001)");
        text.AppendLine();
        text.AppendLine("| | Farol | grep + build | Difference | Required | Result |");
        text.AppendLine("|---|---:|---:|---:|---|---|");
        text.AppendLine(Invariant($"| Success rate | {Percent(Rate(farol))} ({farol.Count(r => r.Success)}/{farol.Count}) | {Percent(Rate(baseline))} ({baseline.Count(r => r.Success)}/{baseline.Count}) | {Points(gain)} | at least +10 pts | {Verdict(gain >= RequiredSuccessGain)} |"));
        text.AppendLine(Invariant($"| Tokens per task | {Tokens(MeanTokens(farol))} | {Tokens(MeanTokens(baseline))} | {Percent(-reduction, signed: true)} | at most −20% | {Verdict(reduction >= RequiredTokenReduction)} |"));
        text.AppendLine(Invariant($"| Cost per task | ${Mean(farol, r => r.CostUsd):N2} | ${Mean(baseline, r => r.CostUsd):N2} | | | |"));
        text.AppendLine(Invariant($"| Turns per task | {Mean(farol, r => r.Turns):N1} | {Mean(baseline, r => r.Turns):N1} | | | |"));
        text.AppendLine(Invariant($"| Minutes per task | {Mean(farol, r => r.DurationSeconds) / 60:N1} | {Mean(baseline, r => r.DurationSeconds) / 60:N1} | | | |"));
        text.AppendLine();
        text.AppendLine(met ? "**Exit criterion met.**" : "**Exit criterion not met.**");
        text.AppendLine();

        text.AppendLine("## By job");
        text.AppendLine();
        text.AppendLine("| Job | Farol success | Baseline success | Farol tokens | Baseline tokens |");
        text.AppendLine("|---|---:|---:|---:|---:|");
        foreach (var job in valid.GroupBy(r => r.Job).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var f = job.Where(r => r.Arm == "farol").ToList();
            var b = job.Where(r => r.Arm == "baseline").ToList();
            text.AppendLine(Invariant($"| {job.Key} | {Percent(Rate(f))} | {Percent(Rate(b))} | {Tokens(MeanTokens(f))} | {Tokens(MeanTokens(b))} |"));
        }

        text.AppendLine();
        text.AppendLine("## By task");
        text.AppendLine();
        text.AppendLine("| Task | Repository | Farol | Baseline | Farol tokens | Baseline tokens | Farol runs using its tools |");
        text.AppendLine("|---|---|---:|---:|---:|---:|---:|");
        foreach (var task in valid.GroupBy(r => r.Task).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var f = task.Where(r => r.Arm == "farol").ToList();
            var b = task.Where(r => r.Arm == "baseline").ToList();
            text.AppendLine(Invariant($"| {task.Key} | {task.First().Repo} | {f.Count(r => r.Success)}/{f.Count} | {b.Count(r => r.Success)}/{b.Count} | {Tokens(MeanTokens(f))} | {Tokens(MeanTokens(b))} | {f.Count(r => r.FarolCalls > 0)}/{f.Count} |"));
        }

        var problems = runs.Where(r => !r.Valid || r.Exit != "completed").ToList();
        if (problems.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("## Runs with problems");
            text.AppendLine();
            foreach (var run in problems)
            {
                var why = run.Error ?? run.IsolationProblem ?? (run.RateLimited ? "usage limit reached" : run.Exit);
                text.AppendLine(Invariant($"- {run.Task} {run.Arm} #{run.Run}: {why}{(run.Valid ? " (counted as a failure)" : " (not counted)")}"));
            }
        }

        var markdown = text.ToString();
        File.WriteAllText(Path.Combine(directory, "summary.md"), markdown);
        return (markdown, met);
    }

    private static double Rate(List<RunRecord> runs) => runs.Count == 0 ? 0 : (double)runs.Count(r => r.Success) / runs.Count;

    private static double MeanTokens(List<RunRecord> runs) => Mean(runs, r => r.Tokens.Total);

    private static double Mean(List<RunRecord> runs, Func<RunRecord, double> value) => runs.Count == 0 ? 0 : runs.Average(value);

    private static string Percent(double value, bool signed = false) =>
        (signed && value > 0 ? "+" : string.Empty) + value.ToString("P0", CultureInfo.InvariantCulture).Replace(" ", string.Empty, StringComparison.Ordinal);

    private static string Points(double value) => Invariant($"{(value >= 0 ? "+" : string.Empty)}{value * 100:N0} pts");

    private static string Tokens(double value) => value >= 1_000_000 ? Invariant($"{value / 1_000_000:N2}M") : Invariant($"{value / 1000:N0}k");

    private static string Verdict(bool passed) => passed ? "pass" : "**fail**";

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
