using System.ComponentModel;
using Farol.Core;
using Farol.Core.Paths;
using Farol.Core.Text;
using Farol.Engine.Navigation;
using Farol.Engine.Workspaces;
using Farol.Tools.Infrastructure;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Farol.Tools.Features.Navigation;

[McpServerToolType]
public sealed class HierarchyTool(WorkspaceManager workspaces)
{
    [McpServerTool(Name = "dotnet_hierarchy", Title = "Type and member hierarchy (C#/VB)", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(HierarchyOutput))]
    [Description(
        "Inheritance around a C#/VB type or member: base types and implemented interfaces, derived types, implementations of an interface, " +
        "and for members the overridden/implemented members, overrides and implementations. Use to find every implementation before changing a contract.")]
    public Task<CallToolResult> Run(
        [Description(SymbolArgument.Description)] string symbol,
        [Description("all (default), base (upwards), derived (downwards, including implementations) or implementations.")] string direction = "all",
        [McpHeader(ToolParameters.WorkspaceHeader), Description(ToolParameters.WorkspaceDescription)] string? workspace = null,
        [Description(ToolParameters.MaxTokensDescription)] int maxTokens = TokenBudget.DefaultTokens,
        CancellationToken cancellationToken = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var root = workspaces.RootDirectory;
            var session = workspaces.GetSession(workspace, SymbolLocator.LocationPath(symbol));
            if (IndexedAnswers.Answer(workspaces, session, indexed => FromIndex(indexed, workspaces.Paths, symbol, direction, maxTokens)) is { } answer)
            {
                return answer;
            }

            var snapshot = await session.GetSnapshotAsync(wait: true, cancellationToken);
            var (candidate, ambiguity, candidates) = await SymbolArgument.ResolveAsync(snapshot, workspaces.Paths, symbol, cancellationToken);
            if (candidate is null)
            {
                return ToolOutput.Of(ambiguity!, new HierarchyOutput(null, direction, null, candidates));
            }

            var item = NavigationItems.Of(candidate, snapshot, root);

            var entries = await HierarchyFinder.FindAsync(snapshot.Solution, candidate, direction, cancellationToken);
            var text = new ResponseBuilder(maxTokens);
            text.Line($"hierarchy of {SymbolFormatter.Display(candidate.Symbol)} (direction: {direction})");
            if (entries.Count == 0)
            {
                text.Line("nothing found in that direction.");
                return ToolOutput.Of(text.ToString(), new HierarchyOutput(item, direction, new Page<RelatedItem>(0, 0, null, []), null));
            }

            text.List("related", entries, e => $"{e.Relation} · {NavigationText.Symbol(e.Symbol, root)}", continuation: _ => "narrow the direction or raise maxTokens");
            return ToolOutput.Of(text.ToString(), new HierarchyOutput(
                item, direction, Page.From(entries, text.WindowOf("related"), e => new RelatedItem(e.Relation, NavigationItems.Of(e.Symbol, root))), null));
        });

    private static ToolOutput<HierarchyOutput>? FromIndex(IndexedAnswers indexed, PathSandbox paths, string symbol, string direction, int maxTokens)
    {
        if (!HierarchyFinder.Directions.Contains(direction.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            throw new FarolException(ErrorCodes.InvalidArgument, $"Unknown direction '{direction.Trim().ToUpperInvariant()}'.", $"Use one of: {string.Join(", ", HierarchyFinder.Directions)}.");
        }

        if (indexed.Resolve(paths, symbol) is not var (candidate, ambiguity, candidates))
        {
            return null;
        }

        if (candidate is null)
        {
            return ToolOutput.Of(ambiguity!, new HierarchyOutput(null, direction, null, candidates) { Index = indexed.Note });
        }

        var item = indexed.Item(candidate);
        var entries = indexed.Index.Hierarchy(candidate, direction);
        var text = new ResponseBuilder(maxTokens);
        indexed.Header(text);
        text.Line($"hierarchy of {candidate.Display} (direction: {direction})");
        if (entries.Count == 0)
        {
            text.Line("nothing found in that direction.");
            return ToolOutput.Of(text.ToString(), new HierarchyOutput(item, direction, new Page<RelatedItem>(0, 0, null, []), null) { Index = indexed.Note });
        }

        text.List("related", entries, e => $"{e.Relation} · {e.Symbol.Kind} {e.Signature} · {indexed.Location(e.Symbol)}{indexed.Mark(e.Symbol.FilePath)}", continuation: _ => "narrow the direction or raise maxTokens");
        return ToolOutput.Of(text.ToString(), new HierarchyOutput(
            item, direction, Page.From(entries, text.WindowOf("related"), e => new RelatedItem(e.Relation, indexed.Item(e.Symbol) with { Symbol = e.Signature })), null) { Index = indexed.Note });
    }
}
