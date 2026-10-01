using System.ComponentModel;
using Farol.Core;
using Farol.Core.Text;
using Farol.Engine.Navigation;
using Farol.Engine.Testing;
using Farol.Engine.Workspaces;
using Farol.Tools.Features.Navigation;
using Farol.Tools.Infrastructure;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Farol.Tools.Features.BuildAndTest;

[McpServerToolType]
public sealed partial class TestTool(WorkspaceManager workspaces, TestRunner tests, ServerPermissions permissions, CallerContext caller, ILogger<TestTool> logger)
{
    private const string Changes = "changes";

    [McpServerTool(Name = "dotnet_test", Title = "Run .NET tests", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(
        "Runs tests — all, those whose name contains a text, or only the ones affected by a symbol or by your edits since the workspace loaded — and " +
        "reports failures only: the assertion message and the stack frames in your code. Builds first with the right toolchain; runs classic " +
        ".NET Framework tests with vstest.console and SDK-style ones with dotnet test (VSTest or Microsoft.Testing.Platform).")]
    public Task<string> Run(
        [Description("Test project name; default: every test project.")] string? project = null,
        [Description("Only tests whose fully qualified name contains this text.")] string? filter = null,
        [Description("Only tests that reach this symbol (name, dotted name, id or path:line), or 'changes' for the members edited since the workspace loaded.")] string? affectedBy = null,
        [Description("Build configuration. Default: Debug.")] string configuration = "Debug",
        [McpHeader(ToolParameters.WorkspaceHeader), Description(ToolParameters.WorkspaceDescription)] string? workspace = null,
        [Description(ToolParameters.MaxTokensDescription)] int maxTokens = TokenBudget.DefaultTokens,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default) =>
        ToolGuard.RunAsync(async () =>
        {
            permissions.Demand("run tests", "Use dotnet_check to verify edits without running code.");
            var root = workspaces.RootDirectory;
            var session = workspaces.GetSession(workspace);
            var snapshot = await session.GetSnapshotAsync(wait: true, cancellationToken);

            (IReadOnlyList<SymbolVariant> Roots, string Subject)? affected = null;
            if (string.Equals(affectedBy?.Trim(), Changes, StringComparison.OrdinalIgnoreCase))
            {
                affected = (await ChangedMembers.SinceLoadAsync(snapshot, cancellationToken), "the members edited since the workspace loaded");
            }
            else if (!string.IsNullOrWhiteSpace(affectedBy))
            {
                var (candidate, ambiguity) = await SymbolArgument.ResolveAsync(snapshot, workspaces.Paths, affectedBy, cancellationToken);
                if (candidate is null)
                {
                    return ambiguity!;
                }

                affected = (candidate.Variants, SymbolFormatter.Display(candidate.Symbol));
            }

            var selection = await TestSelector.SelectAsync(snapshot, project, filter, affected, cancellationToken);
            if (selection.Targets.Count == 0)
            {
                return $"no tests to run: {selection.Description}.";
            }

            LogTests(logger, selection.Description, caller.User, caller.Origin);
            var runs = await tests.RunAsync(snapshot, session.Target, selection.Targets, configuration.Trim(), root, workspaces.Paths.Contains, new ToolProgress(progress), cancellationToken);

            var text = new ResponseBuilder(maxTokens);
            TestRenderer.Render(text, runs, selection, root);
            return text.ToString();
        });

    [LoggerMessage(Level = LogLevel.Information, Message = "Test run ({Selection}) requested by {User} via {Origin}")]
    private static partial void LogTests(ILogger logger, string selection, string user, string origin);
}
