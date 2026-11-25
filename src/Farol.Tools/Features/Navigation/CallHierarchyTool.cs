using System.ComponentModel;
using Farol.Core;
using Farol.Core.Paths;
using Farol.Engine.Indexing;
using Farol.Core.Text;
using Farol.Engine.Navigation;
using Farol.Engine.Workspaces;
using Farol.Tools.Infrastructure;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Farol.Tools.Features.Navigation;

[McpServerToolType]
public sealed class CallHierarchyTool(WorkspaceManager workspaces)
{
    [McpServerTool(Name = "dotnet_call_hierarchy", Title = "Callers or callees of a C#/VB member", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(CallHierarchyOutput))]
    [Description(
        "Who calls a C#/VB method, constructor or property (callers), or what it calls (callees), as a tree up to depth 3 with the call sites. " +
        "Use to trace the impact of a change or the path a request takes through the code.")]
    public Task<CallToolResult> Run(
        [Description(SymbolArgument.Description)] string symbol,
        [Description("callers (default) or callees.")] string direction = "callers",
        [Description("Levels to follow, 1–3 (default 1).")] int depth = 1,
        [McpHeader(ToolParameters.WorkspaceHeader), Description(ToolParameters.WorkspaceDescription)] string? workspace = null,
        [Description(ToolParameters.MaxTokensDescription)] int maxTokens = TokenBudget.DefaultTokens,
        CancellationToken cancellationToken = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var root = workspaces.RootDirectory;
            var session = workspaces.GetSession(workspace, SymbolLocator.LocationPath(symbol));
            var shownDirection = direction.Trim().ToLowerInvariant();
            var shownDepth = Math.Clamp(depth, 1, CallHierarchyFinder.MaxDepth);
            if (IndexedAnswers.Answer(workspaces, session, indexed => FromIndex(indexed, workspaces.Paths, symbol, shownDirection, shownDepth, maxTokens)) is { } answer)
            {
                return answer;
            }

            var snapshot = await session.GetSnapshotAsync(wait: true, cancellationToken);
            var (candidate, ambiguity, candidates) = await SymbolArgument.ResolveAsync(snapshot, workspaces.Paths, symbol, cancellationToken);
            if (candidate is null)
            {
                return ToolOutput.Of(ambiguity!, new CallHierarchyOutput(null, shownDirection, shownDepth, null, 0, candidates));
            }

            var item = NavigationItems.Of(candidate, snapshot, root);

            var nodes = await CallHierarchyFinder.FindAsync(snapshot.Solution, candidate, direction, depth, cancellationToken);
            var text = new ResponseBuilder(maxTokens);
            text.Line($"{direction.Trim().ToLowerInvariant()} of {SymbolFormatter.Display(candidate.Symbol)} (depth {Math.Clamp(depth, 1, CallHierarchyFinder.MaxDepth)})");
            if (nodes.Count == 0)
            {
                text.Line("none found in the workspace.");
                return ToolOutput.Of(text.ToString(), new CallHierarchyOutput(item, shownDirection, shownDepth, [], 0, null));
            }

            var shown = new List<CallItem>();
            var omitted = Write(text, nodes, root, level: 0, parent: null, shown);
            text.More(omitted, "raise maxTokens or lower depth");
            return ToolOutput.Of(text.ToString(), new CallHierarchyOutput(item, shownDirection, shownDepth, shown, omitted, null));
        });

    private static ToolOutput<CallHierarchyOutput>? FromIndex(IndexedAnswers indexed, PathSandbox paths, string symbol, string direction, int depth, int maxTokens)
    {
        if (!CallHierarchyFinder.Directions.Contains(direction, StringComparer.Ordinal))
        {
            throw new FarolException(ErrorCodes.InvalidArgument, $"Unknown direction '{direction}'.", "Use callers or callees.");
        }

        if (indexed.Resolve(paths, symbol) is not var (candidate, ambiguity, candidates))
        {
            return null;
        }

        if (candidate is null)
        {
            return ToolOutput.Of(ambiguity!, new CallHierarchyOutput(null, direction, depth, null, 0, candidates) { Index = indexed.Note });
        }

        var item = indexed.Item(candidate);
        var nodes = indexed.Index.Calls(candidate, direction, depth);
        var text = new ResponseBuilder(maxTokens);
        indexed.Header(text);
        text.Line($"{direction} of {candidate.Display} (depth {depth})");
        if (nodes.Count == 0)
        {
            text.Line("none found in the workspace.");
            return ToolOutput.Of(text.ToString(), new CallHierarchyOutput(item, direction, depth, [], 0, null) { Index = indexed.Note });
        }

        var shown = new List<CallItem>();
        var omitted = Write(text, nodes, indexed, level: 0, parent: null, shown);
        text.More(omitted, "raise maxTokens or lower depth");
        return ToolOutput.Of(text.ToString(), new CallHierarchyOutput(item, direction, depth, shown, omitted, null) { Index = indexed.Note });
    }

    private static int Write(ResponseBuilder text, IReadOnlyList<IndexedCallNode> nodes, IndexedAnswers indexed, int level, int? parent, List<CallItem> shown)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            var siteList = node.Sites.Take(5).Select(s => DisplayPath.Location(indexed.Root, s.FilePath, s.Line)).ToList();
            var sites = node.Sites.Count == 0 ? string.Empty : " · at " + string.Join(", ", siteList);
            var changed = node.Sites.Any(s => indexed.Freshness.IsChanged(s.FilePath)) ? IndexedAnswers.ChangedMark : indexed.Mark(node.Symbol.FilePath);
            if (!text.TryLine($"{new string(' ', level * 2)}- {node.Symbol.Display} · {indexed.Location(node.Symbol)}{sites}{changed}"))
            {
                return nodes.Count - i;
            }

            shown.Add(new CallItem(level, parent, indexed.Item(node.Symbol), siteList));
            var omitted = Write(text, node.Children, indexed, level + 1, shown.Count - 1, shown);
            if (omitted > 0)
            {
                return omitted + (nodes.Count - i - 1);
            }
        }

        return 0;
    }

    // Returns how many nodes did not fit; the ones written go to 'shown', in text order.
    private static int Write(ResponseBuilder text, IReadOnlyList<CallNode> nodes, string root, int level, int? parent, List<CallItem> shown)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            var siteList = node.Sites.Take(5).Select(s => DisplayPath.Location(root, s.FilePath, s.Line)).ToList();
            var sites = node.Sites.Count == 0 ? string.Empty : " · at " + string.Join(", ", siteList);
            if (!text.TryLine($"{new string(' ', level * 2)}- {NavigationText.Symbol(node.Symbol, root)}{sites}"))
            {
                return nodes.Count - i;
            }

            shown.Add(new CallItem(level, parent, NavigationItems.Of(node.Symbol, root), siteList));
            var omitted = Write(text, node.Children, root, level + 1, shown.Count - 1, shown);
            if (omitted > 0)
            {
                return omitted + (nodes.Count - i - 1);
            }
        }

        return 0;
    }
}
