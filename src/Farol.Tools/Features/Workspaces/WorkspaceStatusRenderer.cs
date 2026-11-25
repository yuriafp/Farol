using System.Globalization;
using Farol.Core.Paths;
using Farol.Core.Text;
using Farol.Engine.Toolchain;
using Farol.Engine.Workspaces;

using Farol.Tools.Infrastructure;

namespace Farol.Tools.Features.Workspaces;

internal static class WorkspaceStatusRenderer
{
    public static ToolOutput<WorkspaceOutput> Render(WorkspaceSession session, ToolchainInfo toolchain, string root, bool readOnly, DateTimeOffset? indexWrittenAt = null)
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
                if (session.IndexWrittenAt is { } indexed)
                {
                    text.Line($"index: written {Utc(indexed)}, kept up to date in the background for the next start");
                }

                break;
            case WorkspaceState.Loading:
                // An agent told only to call again went back to grep (AC-45): say that the other tools wait, and for about how long.
                var last = session.LastLoadTook is { } took ? $" · the last load here took {Seconds(took)}" : string.Empty;
                text.Line($"state: loading · {session.EvaluationSteps} project evaluation step(s) done · {Seconds(session.LoadingFor)} elapsed{last}");
                text.Line(indexWrittenAt is { } from
                    ? $"hint: navigation and dotnet_overview answer now from the index written {Utc(from)}; dotnet_check, code actions, build and test wait for the load, so call the one you need now."
                    : "hint: the other dotnet_* tools wait for the load to finish, so call the one you need now; action='load' waits for it here.");
                break;
            case WorkspaceState.Failed:
                text.Line($"state: failed · {DisplayPath.InText(root, session.FailureMessage ?? string.Empty)}");
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

        var state = session.State;
        var ready = state == WorkspaceState.Ready && report is not null;
        var data = new WorkspaceOutput(
            DisplayPath.From(root, session.Target.Path),
            state switch { WorkspaceState.Ready => "ready", WorkspaceState.Loading => "loading", WorkspaceState.Failed => "failed", _ => "not loaded" },
            ready ? report!.Elapsed.TotalSeconds : null,
            ready ? report!.Loader : null,
            ready ? report!.ProjectFiles : null,
            ready ? report!.Documents : null,
            ready ? session.WarmedIn?.TotalSeconds : null,
            ready && session.IsWarming,
            ready ? session.CurrentSnapshot?.Version : null,
            ready ? session.TracksFileChanges : null,
            state == WorkspaceState.Loading ? session.LoadingFor.TotalSeconds : null,
            state == WorkspaceState.Loading ? session.LastLoadTook?.TotalSeconds : null,
            state == WorkspaceState.Failed && session.FailureMessage is { } failure ? DisplayPath.InText(root, failure) : null,
            toolchain.DotnetSdkVersion,
            toolchain.PreferredVisualStudio is { } vs ? $"{vs.DisplayName} {vs.ShortVersion}" : null,
            readOnly,
            report is { Issues.Count: > 0 }
                ? Page.From(report.Issues, text.WindowOf("load issues"), i => new LoadIssueItem(i.Severity.ToString().ToLowerInvariant(), DisplayPath.InText(root, i.Message)))
                : new Page<LoadIssueItem>(0, 0, null, []));
        return ToolOutput.Of(text.ToString(), data with { IndexWrittenAt = (session.IndexWrittenAt ?? indexWrittenAt)?.ToString("O", CultureInfo.InvariantCulture) });
    }

    private static string Utc(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";

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

/// <summary>Structured output of dotnet_workspace. Fields of a state the workspace is not in are null.</summary>
public sealed record WorkspaceOutput(
    string Workspace,
    string State,
    double? LoadedInSeconds,
    string? Loader,
    int? Projects,
    int? Documents,
    double? WarmedInSeconds,
    bool Warming,
    int? SnapshotVersion,
    bool? TracksFileChanges,
    double? LoadingForSeconds,
    double? LastLoadSeconds,
    string? Failure,
    string? DotnetSdk,
    string? VisualStudio,
    bool ReadOnly,
    Page<LoadIssueItem> LoadIssues)
{
    /// <summary>When the persistent index was written: by this server once loaded, or the one answering while the load runs.</summary>
    public string? IndexWrittenAt { get; init; }
}

public sealed record LoadIssueItem(string Severity, string Message);
