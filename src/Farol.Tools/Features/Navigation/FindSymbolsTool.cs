using System.ComponentModel;
using Farol.Core.Text;
using Farol.Engine.Navigation;
using Farol.Engine.Workspaces;
using Farol.Tools.Infrastructure;
using ModelContextProtocol.Server;

namespace Farol.Tools.Features.Navigation;

[McpServerToolType]
public sealed class FindSymbolsTool(WorkspaceManager workspaces)
{
    [McpServerTool(Name = "dotnet_find_symbols", Title = "Find C#/VB declarations", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Finds where C#/VB types and members are declared, by name: exact, prefix, substring or camel-case humps ('OrdCalc' finds OrderCalculator). " +
        "Use instead of grep to locate a declaration: it ignores comments, strings and usages. Returns path:line and an id to pass to other dotnet_* tools.")]
    public Task<string> Run(
        [Description("Name or pattern to search for.")] string query,
        [Description("any (default), type, class, interface, struct, enum, record, delegate, method, property, field or event.")] string kind = "any",
        [Description("Only declarations in this project (name as shown by dotnet_overview).")] string? project = null,
        [McpHeader(ToolParameters.WorkspaceHeader), Description(ToolParameters.WorkspaceDescription)] string? workspace = null,
        [Description(ToolParameters.MaxTokensDescription)] int maxTokens = TokenBudget.DefaultTokens,
        [Description(ToolParameters.OffsetDescription)] int offset = 0,
        CancellationToken cancellationToken = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var snapshot = await workspaces.GetSession(workspace).GetSnapshotAsync(wait: true, cancellationToken);
            var candidates = await DeclarationSearch.SearchAsync(snapshot, query, kind, project, cancellationToken);
            var text = new ResponseBuilder(maxTokens);
            if (candidates.Count == 0)
            {
                return $"no declarations match '{query}' (kind: {kind}). Try a shorter pattern or kind='any'.";
            }

            text.List($"symbols matching '{query}'", candidates, c => NavigationText.Candidate(c, snapshot, workspaces.RootDirectory), offset, ResponseBuilder.OffsetHint);
            return text.ToString();
        });
}
