using Farol.Core.Text;
using Farol.Engine.Testing;

using Farol.Tools.Infrastructure;

namespace Farol.Tools.Features.BuildAndTest;

/// <summary>Test results for agents: totals and selection first, then only what failed, with the frames in the user's code.</summary>
internal static class TestRenderer
{
    public static TestOutput Render(ResponseBuilder text, IReadOnlyList<TestProjectRun> runs, TestSelection selection, string root)
    {
        var total = runs.Sum(r => r.Total);
        var failed = runs.Sum(r => r.Failed);
        var passed = runs.Sum(r => r.Passed);
        var skipped = runs.Sum(r => r.Skipped);
        var broken = runs.Count(r => r.FailedBuild is not null || (r.Problem is not null && r.Total == 0));
        var verdict = broken > 0 ? $"{broken} project(s) could not run" : failed > 0 ? "failed" : total == 0 ? "no test ran" : "passed";
        text.Line($"tests: {verdict} · {failed} failed, {passed} passed, {skipped} skipped · {total} run in {runs.Count} project(s) · {BuildRenderer.Seconds(TimeSpan.FromTicks(runs.Sum(r => r.Elapsed.Ticks)))}");
        text.Line($"selection: {selection.Description}");

        var items = new List<TestRunItem>();
        foreach (var run in runs)
        {
            if (run.FailedBuild is { } build)
            {
                text.Line($"{run.Project} · build failed ({build.Toolchain}): {BuildRenderer.Counts(build)}");
                var withoutWarnings = build with { Warnings = [] };
                var shownIssues = BuildRenderer.Issues(text, withoutWarnings, root);
                items.Add(new TestRunItem(run.Project, run.Runner, run.Failed, run.Passed, run.Skipped, run.Note, run.Problem, BuildRenderer.Output(withoutWarnings, root, shownIssues), new Page<TestFailureItem>(0, 0, null, [])));
                continue;
            }

            text.Line($"{run.Project} · {run.Runner} · {run.Failed} failed, {run.Passed} passed, {run.Skipped} skipped");
            if (run.Note is { } note)
            {
                text.Line("note: " + note);
            }

            if (run.Problem is { } problem)
            {
                text.Line("problem: " + problem);
            }

            var shown = Failures(text, run.Failures);
            items.Add(new TestRunItem(
                run.Project, run.Runner, run.Failed, run.Passed, run.Skipped, run.Note, run.Problem, null,
                new Page<TestFailureItem>(run.Failures.Count, 0, shown < run.Failures.Count ? shown : null, [.. run.Failures.Take(shown).Select(f => new TestFailureItem(f.Name, f.Message, f.UserFrames))])));
        }

        var status = broken > 0 ? "broken" : failed > 0 ? "failed" : total == 0 ? "none" : "passed";
        return new TestOutput(status, verdict, failed, passed, skipped, total, TimeSpan.FromTicks(runs.Sum(r => r.Elapsed.Ticks)).TotalSeconds, selection.Description, items);
    }

    private static int Failures(ResponseBuilder text, IReadOnlyList<TestFailure> failures)
    {
        var written = 0;
        foreach (var failure in failures)
        {
            var lines = new List<string> { $"- failed {failure.Name}" };
            lines.AddRange(failure.Message.Split('\n').Select(l => "  " + l));
            lines.AddRange(failure.UserFrames.Select(f => "  " + f));
            if (!lines.All(text.TryLine))
            {
                break;
            }

            written++;
        }

        text.More(failures.Count - written, "failures; raise maxTokens or narrow the run with filter");
        return written;
    }
}

/// <summary>Structured output of dotnet_test; failures are the ones the text lists.</summary>
public sealed record TestOutput([property: System.ComponentModel.Description("passed, failed, broken (a project could not run), none (no test ran) or ambiguous (the symbol named several).")] string Status, string Verdict, int Failed, int Passed, int Skipped, int Total, double Seconds, string Selection, IReadOnlyList<TestRunItem> Projects);

public sealed record TestRunItem(
    string Project,
    string Runner,
    int Failed,
    int Passed,
    int Skipped,
    string? Note,
    string? Problem,
    BuildOutput? FailedBuild,
    Page<TestFailureItem> Failures);

public sealed record TestFailureItem(string Name, string Message, IReadOnlyList<string> Frames);
