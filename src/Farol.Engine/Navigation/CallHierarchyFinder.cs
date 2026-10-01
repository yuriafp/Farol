using Farol.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;

namespace Farol.Engine.Navigation;

public sealed record CallSite(string FilePath, int Line);

public sealed record CallNode(ISymbol Symbol, IReadOnlyList<CallSite> Sites, IReadOnlyList<CallNode> Children);

/// <summary>
/// Callers (through Roslyn's caller search) and callees (through <see cref="IOperation"/>, so C# and VB
/// share one implementation), up to a depth, with a node budget that keeps big graphs bounded.
/// </summary>
public static class CallHierarchyFinder
{
    public const int MaxDepth = 3;
    public const int MaxNodes = 200;

    public static readonly IReadOnlyList<string> Directions = ["callers", "callees"];

    public static async Task<IReadOnlyList<CallNode>> FindAsync(Solution solution, SymbolCandidate candidate, string direction, int depth, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var budget = new NodeBudget(MaxNodes);
        var visited = new HashSet<string>(StringComparer.Ordinal) { candidate.Id };
        var targets = candidate.Variants.Select(v => v.Symbol).ToList();
        depth = Math.Clamp(depth, 1, MaxDepth);
        return direction.Trim().ToUpperInvariant() switch
        {
            "CALLERS" => await CallersAsync(solution, targets, depth, visited, budget, cancellationToken),
            "CALLEES" => await CalleesAsync(solution, targets, depth, visited, budget, cancellationToken),
            _ => throw new FarolException(ErrorCodes.InvalidArgument, $"Unknown direction '{direction}'.", "Use callers or callees."),
        };
    }

    private static async Task<IReadOnlyList<CallNode>> CallersAsync(
        Solution solution, IReadOnlyList<ISymbol> targets, int depth, HashSet<string> visited, NodeBudget budget, CancellationToken cancellationToken)
    {
        var callers = new Dictionary<string, (List<ISymbol> Variants, List<Location> Sites)>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            foreach (var caller in await SymbolFinder.FindCallersAsync(target, solution, cancellationToken))
            {
                var calling = EnclosingMember(caller.CallingSymbol);
                var id = SymbolFormatter.Id(calling);
                if (!callers.TryGetValue(id, out var entry))
                {
                    entry = ([], []);
                    callers[id] = entry;
                }

                entry.Variants.Add(calling);
                entry.Sites.AddRange(caller.Locations.Where(l => l.IsInSource));
            }
        }

        var nodes = new List<CallNode>();
        foreach (var (id, (variants, sites)) in callers.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!budget.TryTake())
            {
                break;
            }

            var children = depth > 1 && visited.Add(id)
                ? await CallersAsync(solution, variants, depth - 1, visited, budget, cancellationToken)
                : [];
            nodes.Add(new CallNode(variants[0], ToSites(sites), children));
        }

        return nodes;
    }

    private static async Task<IReadOnlyList<CallNode>> CalleesAsync(
        Solution solution, IReadOnlyList<ISymbol> sources, int depth, HashSet<string> visited, NodeBudget budget, CancellationToken cancellationToken)
    {
        var callees = new Dictionary<string, (ISymbol Symbol, List<Location> Sites)>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            foreach (var (callee, site) in await DirectCalleesAsync(solution, source, cancellationToken))
            {
                var id = SymbolFormatter.Id(callee);
                if (!callees.TryGetValue(id, out var entry))
                {
                    entry = (callee, []);
                    callees[id] = entry;
                }

                entry.Sites.Add(site);
            }
        }

        var nodes = new List<CallNode>();
        foreach (var (id, (symbol, sites)) in callees.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!budget.TryTake())
            {
                break;
            }

            var children = depth > 1 && symbol.Locations.Any(l => l.IsInSource) && visited.Add(id)
                ? await CalleesAsync(solution, [symbol], depth - 1, visited, budget, cancellationToken)
                : [];
            nodes.Add(new CallNode(symbol, ToSites(sites), children));
        }

        return nodes;
    }

    private static async Task<List<(ISymbol Callee, Location Site)>> DirectCalleesAsync(Solution solution, ISymbol symbol, CancellationToken cancellationToken)
    {
        var result = new List<(ISymbol, Location)>();
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            var node = await reference.GetSyntaxAsync(cancellationToken);
            if (solution.GetDocument(node.SyntaxTree) is not { } document || await document.GetSemanticModelAsync(cancellationToken) is not { } model)
            {
                continue;
            }

            // C# declares methods on the whole declaration node; VB on the statement inside the method block.
            var body = model.GetOperation(node, cancellationToken) ?? (node.Parent is { } parent ? model.GetOperation(parent, cancellationToken) : null);
            if (body is null)
            {
                continue;
            }

            foreach (var operation in body.Descendants())
            {
                ISymbol? callee = operation switch
                {
                    IInvocationOperation invocation => invocation.TargetMethod,
                    IObjectCreationOperation creation => creation.Constructor,
                    _ => null,
                };
                if (callee is not null)
                {
                    result.Add((callee.OriginalDefinition, operation.Syntax.GetLocation()));
                }
            }
        }

        return result;
    }

    // A call inside a lambda is reported with the lambda as caller; the member that contains it is what agents need.
    private static ISymbol EnclosingMember(ISymbol symbol)
    {
        var current = symbol;
        while (current is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction } && current.ContainingSymbol is { } container)
        {
            current = container;
        }

        return current;
    }

    private static List<CallSite> ToSites(IEnumerable<Location> locations) =>
        [.. locations
            .Where(l => l.SourceTree?.FilePath is { Length: > 0 })
            .Select(l => new CallSite(l.SourceTree!.FilePath, l.GetLineSpan().StartLinePosition.Line + 1))
            .Distinct()
            .OrderBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Line)];

    private sealed class NodeBudget(int remaining)
    {
        public bool TryTake() => remaining-- > 0;
    }
}
