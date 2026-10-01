using System.ComponentModel;
using Farol.Core.Text;
using Farol.Engine.Navigation;
using Farol.Engine.Workspaces;
using Farol.Tools.Infrastructure;
using ModelContextProtocol.Server;

namespace Farol.Tools.Features.Navigation;

[McpServerToolType]
public sealed class HierarchyTool(WorkspaceManager workspaces)
{
    [McpServerTool(Name = "dotnet_hierarchy", Title = "Type and member hierarchy (C#/VB)", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Inheritance around a C#/VB type or member: base types and implemented interfaces, derived types, implementations of an interface, " +
        "and for members the overridden/implemented members, overrides and implementations. Use to find every implementation before changing a contract.")]
    public Task<string> Run(
        [Description(SymbolArgument.Description)] string symbol,
        [Description("all (default), base (upwards), derived (downwards, including implementations) or implementations.")] string direction = "all",
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

            var entries = await HierarchyFinder.FindAsync(snapshot.Solution, candidate, direction, cancellationToken);
            var text = new ResponseBuilder(maxTokens);
            text.Line($"hierarchy of {SymbolFormatter.Display(candidate.Symbol)} (direction: {direction})");
            if (entries.Count == 0)
            {
                text.Line("nothing found in that direction.");
                return text.ToString();
            }

            text.List("related", entries, e => $"{e.Relation} · {NavigationText.Symbol(e.Symbol, root)}", continuation: _ => "narrow the direction or raise maxTokens");
            return text.ToString();
        });
}
