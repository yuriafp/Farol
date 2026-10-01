using System.ComponentModel;
using Farol.Core;
using Farol.Core.Text;
using Farol.Engine.Diagnostics;
using Farol.Engine.Workspaces;
using Farol.Tools.Infrastructure;
using ModelContextProtocol.Server;

namespace Farol.Tools.Features.Verify;

[McpServerToolType]
public sealed class CheckTool(WorkspaceManager workspaces)
{
    [McpServerTool(Name = "dotnet_check", Title = "Check C#/VB edits for new errors", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Compiler errors and warnings introduced since the workspace loaded — not the ones that were already there — in the edited files and in every " +
        "project their declaration changes can break, across C#, VB and every target framework. Call it after editing .cs/.vb files instead of " +
        "building: edits on disk are picked up automatically, and it takes about a second. Covers compiler diagnostics; analyzer rules (CA/IDE) show up in dotnet_build.")]
    public Task<string> Run(
        [Description("changed (default): files edited since load, plus the projects that depend on their declarations · file: one file ('path') · project: one project ('project') · solution: every project")] string? scope = null,
        [Description("Source file for scope=file, relative to the workspace root.")] string? path = null,
        [Description("Project name for scope=project, as dotnet_overview lists it.")] string? project = null,
        [Description("Also list, separately, the diagnostics that already existed when the workspace loaded. Default false.")] bool includeExisting = false,
        [McpHeader(ToolParameters.WorkspaceHeader), Description(ToolParameters.WorkspaceDescription)] string? workspace = null,
        [Description(ToolParameters.MaxTokensDescription)] int maxTokens = TokenBudget.DefaultTokens,
        [Description(ToolParameters.OffsetDescription)] int offset = 0,
        CancellationToken cancellationToken = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var kind = ParseScope(scope, path, project);
            var fullPath = kind == CheckScope.File && !string.IsNullOrWhiteSpace(path) ? workspaces.Paths.Resolve(path) : null;
            var snapshot = await workspaces.GetSession(workspace).GetSnapshotAsync(wait: true, cancellationToken);
            var result = await DiagnosticCheck.RunAsync(snapshot, new CheckRequest(kind, fullPath, project, includeExisting), cancellationToken);

            var text = new ResponseBuilder(maxTokens);
            CheckRenderer.Render(text, result, snapshot, workspaces.RootDirectory, kind, offset);
            return text.ToString();
        });

    // Without an explicit scope, a path means one file and a project name means that project.
    private static CheckScope ParseScope(string? scope, string? path, string? project)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return !string.IsNullOrWhiteSpace(path) ? CheckScope.File
                : !string.IsNullOrWhiteSpace(project) ? CheckScope.Project
                : CheckScope.Changed;
        }

        return scope.Trim().ToUpperInvariant() switch
        {
            "CHANGED" => CheckScope.Changed,
            "FILE" => CheckScope.File,
            "PROJECT" => CheckScope.Project,
            "SOLUTION" => CheckScope.Solution,
            _ => throw new FarolException(ErrorCodes.InvalidArgument, $"Unknown scope '{scope}'.", "Use changed, file, project or solution."),
        };
    }
}
