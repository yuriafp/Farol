using Farol.Core.Text;
using Farol.Engine.Testing;

namespace Farol.Tools.Features.BuildAndTest;

/// <summary>Test results for agents: totals and selection first, then only what failed, with the frames in the user's code.</summary>
internal static class TestRenderer
{
    public static void Render(ResponseBuilder text, IReadOnlyList<TestProjectRun> runs, TestSelection selection, string root)
    {
        var total = runs.Sum(r => r.Total);
        var failed = runs.Sum(r => r.Failed);
        var passed = runs.Sum(r => r.Passed);
        var skipped = runs.Sum(r => r.Skipped);
        var broken = runs.Count(r => r.FailedBuild is not null || (r.Problem is not null && r.Total == 0));
        var verdict = broken > 0 ? $"{broken} project(s) could not run" : failed > 0 ? "failed" : total == 0 ? "no test ran" : "passed";
        text.Line($"tests: {verdict} · {failed} failed, {passed} passed, {skipped} skipped · {total} run in {runs.Count} project(s) · {BuildRenderer.Seconds(TimeSpan.FromTicks(runs.Sum(r => r.Elapsed.Ticks)))}");
        text.Line($"selection: {selection.Description}");

        foreach (var run in runs)
        {
            if (run.FailedBuild is { } build)
            {
                text.Line($"{run.Project} · build failed ({build.Toolchain}): {BuildRenderer.Counts(build)}");
                BuildRenderer.Issues(text, build with { Warnings = [] }, root);
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

            Failures(text, run.Failures);
        }
    }

    private static void Failures(ResponseBuilder text, IReadOnlyList<TestFailure> failures)
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
    }
}
