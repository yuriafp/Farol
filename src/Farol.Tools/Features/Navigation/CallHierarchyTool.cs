using System.ComponentModel;
using Farol.Core.Paths;
using Farol.Core.Text;
using Farol.Engine.Navigation;
using Farol.Engine.Workspaces;
using Farol.Tools.Infrastructure;
using ModelContextProtocol.Server;

namespace Farol.Tools.Features.Navigation;

[McpServerToolType]
public sealed class CallHierarchyTool(WorkspaceManager workspaces)
{
    [McpServerTool(Name = "dotnet_call_hierarchy", Title = "Callers or callees of a C#/VB member", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Who calls a C#/VB method, constructor or property (callers), or what it calls (callees), as a tree up to depth 3 with the call sites. " +
        "Use to trace the impact of a change or the path a request takes through the code.")]
    public Task<string> Run(
        [Description(SymbolArgument.Description)] string symbol,
        [Description("callers (default) or callees.")] string direction = "callers",
        [Description("Levels to follow, 1–3 (default 1).")] int depth = 1,
        [McpHeader(ToolParameters.WorkspaceHeader), Description(ToolParameters.WorkspaceDescription)] string? workspace = null,
        [Description(ToolParameters.MaxTokensDescription)] int maxTokens = TokenBudget.DefaultTokens,
        CancellationToken cancellationToken = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var root = workspaces.RootDirectory;
            var snapshot = await workspaces.GetSession(workspace).GetSnapshotAsync(wait: true, cancellationToken);
            var (candidate, ambiguity) = await SymbolArgument.ResolveAsync(snapshot, root, symbol, cancellationToken);
            if (candidate is null)
            {
                return ambiguity!;
            }

            var nodes = await CallHierarchyFinder.FindAsync(snapshot.Solution, candidate, direction, depth, cancellationToken);
            var text = new ResponseBuilder(maxTokens);
            text.Line($"{direction.Trim().ToLowerInvariant()} of {SymbolFormatter.Display(candidate.Symbol)} (depth {Math.Clamp(depth, 1, CallHierarchyFinder.MaxDepth)})");
            if (nodes.Count == 0)
            {
                text.Line("none found in the workspace.");
                return text.ToString();
            }

            var omitted = Write(text, nodes, root, level: 0);
            text.More(omitted, "raise maxTokens or lower depth");
            return text.ToString();
        });

    // Returns how many nodes did not fit.
    private static int Write(ResponseBuilder text, IReadOnlyList<CallNode> nodes, string root, int level)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            var sites = node.Sites.Count == 0 ? string.Empty : " · at " + string.Join(", ", node.Sites.Take(5).Select(s => DisplayPath.Location(root, s.FilePath, s.Line)));
            if (!text.TryLine($"{new string(' ', level * 2)}- {NavigationText.Symbol(node.Symbol, root)}{sites}"))
            {
                return nodes.Count - i;
            }

            var omitted = Write(text, node.Children, root, level + 1);
            if (omitted > 0)
            {
                return omitted + (nodes.Count - i - 1);
            }
        }

        return 0;
    }
}
