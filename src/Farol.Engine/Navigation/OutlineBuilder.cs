using Microsoft.CodeAnalysis;

namespace Farol.Engine.Navigation;

public sealed record OutlineEntry(int Depth, ISymbol Symbol, string FilePath, int Line);

/// <summary>
/// The shape of a file or type — types and members with signatures and lines, no bodies — at a small
/// fraction of the tokens of reading the file.
/// </summary>
public static class OutlineBuilder
{
    public static async Task<IReadOnlyList<OutlineEntry>> ForDocumentAsync(Document document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        var tree = await document.GetSyntaxTreeAsync(cancellationToken);
        var compilation = await document.Project.GetCompilationAsync(cancellationToken);
        if (tree is null || compilation is null)
        {
            return [];
        }

        var entries = new List<OutlineEntry>();
        var types = compilation.GetSymbolsWithName(static _ => true, SymbolFilter.Type, cancellationToken)
            .OfType<INamedTypeSymbol>()
            .Where(t => t.Locations.Any(l => l.SourceTree == tree));
        foreach (var type in types)
        {
            Add(entries, type, tree);
            foreach (var member in OutlineMembers(type))
            {
                Add(entries, member, tree);
            }
        }

        return [.. entries.OrderBy(e => e.Line).ThenBy(e => e.Depth)];
    }

    public static IReadOnlyList<OutlineEntry> ForType(INamedTypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var entries = new List<OutlineEntry>();
        Add(entries, type, tree: null);
        foreach (var member in OutlineMembers(type).Concat(type.GetTypeMembers().SelectMany(t => OutlineMembers(t).Prepend(t))))
        {
            Add(entries, member, tree: null);
        }

        return [.. entries.DistinctBy(e => (e.Symbol, e.Line)).OrderBy(e => e.FilePath, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Line)];
    }

    private static IEnumerable<ISymbol> OutlineMembers(INamedTypeSymbol type) =>
        type.GetMembers().Where(m =>
            !m.IsImplicitlyDeclared
            && m is not INamedTypeSymbol
            && m is not IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise });

    private static void Add(List<OutlineEntry> entries, ISymbol symbol, SyntaxTree? tree)
    {
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource && (tree is null || l.SourceTree == tree));
        if (location?.SourceTree?.FilePath is not { Length: > 0 } path)
        {
            return;
        }

        var depth = 0;
        for (var container = symbol.ContainingType; container is not null; container = container.ContainingType)
        {
            depth++;
        }

        entries.Add(new OutlineEntry(depth, symbol, path, location.GetLineSpan().StartLinePosition.Line + 1));
    }
}
