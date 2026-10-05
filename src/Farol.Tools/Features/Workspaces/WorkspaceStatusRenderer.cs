using System.Globalization;
using Farol.Core.Paths;
using Farol.Core.Text;
using Farol.Engine.Toolchain;
using Farol.Engine.Workspaces;

namespace Farol.Tools.Features.Workspaces;

internal static class WorkspaceStatusRenderer
{
    public static string Render(WorkspaceSession session, ToolchainInfo toolchain, string root, bool readOnly)
    {
        var text = new ResponseBuilder(TokenBudget.DefaultTokens);
        text.Line($"workspace: {DisplayPath.From(root, session.Target.Path)}");

        var report = session.Report;
        switch (session.State)
        {
            case WorkspaceState.Ready when report is not null:
                text.Line($"state: ready · loaded in {Seconds(report.Elapsed)} · loader: {report.Loader}");
                text.Line($"projects: {report.ProjectFiles} ({report.RoslynProjects} including target-framework variants) · documents: {report.Documents}");
                if (session.WarmedIn is { } warmedIn)
                {
                    text.Line($"warm-up: compilations and search indexes ready in {Seconds(warmedIn)}");
                }
                else if (session.IsWarming)
                {
                    text.Line($"warm-up: building compilations and search indexes in the background ({Seconds(session.WarmingFor)} so far); searches are slower until it ends");
                }

                text.Line(session.TracksFileChanges
                    ? $"snapshot: v{session.CurrentSnapshot?.Version} · file changes: tracked automatically"
                    : $"snapshot: v{session.CurrentSnapshot?.Version} · file changes: NOT tracked (call again with action='reload' after edits)");
                break;
            case WorkspaceState.Loading:
                text.Line($"state: loading · {session.EvaluationSteps} project evaluation step(s) done · {Seconds(session.LoadingFor)} elapsed");
                text.Line("hint: call again with action='load' to wait until it is ready.");
                break;
            case WorkspaceState.Failed:
                text.Line($"state: failed · {session.FailureMessage}");
                text.Line("hint: fix the cause, then call again with action='reload'.");
                break;
            default:
                text.Line("state: not loaded · call with action='load'.");
                break;
        }

        text.Line(ToolchainLine(toolchain));
        if (readOnly)
        {
            text.Line("mode: read-only (--read-only): writing files, building and running tests are refused");
        }

        if (report is { Issues.Count: > 0 })
        {
            text.List("load issues", report.Issues, i => $"{i.Severity}: {DisplayPath.InText(root, i.Message)}", continuation: _ => "results for the affected projects may be incomplete");
        }

        return text.ToString();
    }

    public static string ToolchainLine(ToolchainInfo toolchain)
    {
        var sdk = toolchain.DotnetSdkVersion is null ? ".NET SDK not found" : $".NET SDK {toolchain.DotnetSdkVersion}";
        var visualStudio = toolchain.PreferredVisualStudio is { } vs
            ? $"{vs.DisplayName} {vs.ShortVersion} (classic .NET Framework projects: full fidelity)"
            : "no Visual Studio/Build Tools MSBuild (classic .NET Framework projects load with reduced fidelity)";
        return $"toolchain: {sdk} · {visualStudio}";
    }

    private static string Seconds(TimeSpan elapsed) =>
        elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";
}
