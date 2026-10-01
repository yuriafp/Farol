using Farol.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Farol.Engine.Navigation;

public sealed record HierarchyEntry(string Relation, ISymbol Symbol);

/// <summary>Types: bases, interfaces, derived types, implementations. Members: overridden/implemented members, overrides, implementations.</summary>
public static class HierarchyFinder
{
    public static readonly IReadOnlyList<string> Directions = ["all", "base", "derived", "implementations"];

    public static async Task<IReadOnlyList<HierarchyEntry>> FindAsync(Solution solution, SymbolCandidate candidate, string direction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        direction = direction.Trim().ToUpperInvariant();
        if (!Directions.Contains(direction, StringComparer.OrdinalIgnoreCase))
        {
            throw new FarolException(ErrorCodes.InvalidArgument, $"Unknown direction '{direction}'.", $"Use one of: {string.Join(", ", Directions)}.");
        }

        var up = direction is "ALL" or "BASE";
        var down = direction is "ALL" or "DERIVED";
        var implementations = direction is "ALL" or "DERIVED" or "IMPLEMENTATIONS";

        var entries = new List<HierarchyEntry>();
        foreach (var symbol in candidate.Variants.Select(v => v.Symbol))
        {
            if (symbol is INamedTypeSymbol type)
            {
                await AddTypeHierarchyAsync(solution, type, up, down, implementations, entries, cancellationToken);
            }
            else
            {
                await AddMemberHierarchyAsync(solution, symbol, up, down, implementations, entries, cancellationToken);
            }
        }

        return [.. entries.DistinctBy(e => (e.Relation, SymbolFormatter.Id(e.Symbol)))];
    }

    private static async Task AddTypeHierarchyAsync(
        Solution solution, INamedTypeSymbol type, bool up, bool down, bool implementations, List<HierarchyEntry> entries, CancellationToken cancellationToken)
    {
        if (up)
        {
            for (var baseType = type.BaseType; baseType is not null && baseType.SpecialType != SpecialType.System_Object; baseType = baseType.BaseType)
            {
                entries.Add(new HierarchyEntry("base", baseType));
            }

            entries.AddRange(type.AllInterfaces.Select(i => new HierarchyEntry("implements", i)));
        }

        if (down && type.TypeKind == TypeKind.Class)
        {
            var derived = await SymbolFinder.FindDerivedClassesAsync(type, solution, transitive: true, cancellationToken: cancellationToken);
            entries.AddRange(derived.Select(d => new HierarchyEntry("derived", d)));
        }

        if (down && type.TypeKind == TypeKind.Interface)
        {
            var derived = await SymbolFinder.FindDerivedInterfacesAsync(type, solution, transitive: true, cancellationToken: cancellationToken);
            entries.AddRange(derived.Select(d => new HierarchyEntry("derived interface", d)));
        }

        if (implementations && type.TypeKind == TypeKind.Interface)
        {
            var implementing = await SymbolFinder.FindImplementationsAsync(type, solution, transitive: true, cancellationToken: cancellationToken);
            entries.AddRange(implementing.Select(i => new HierarchyEntry("implementation", i)));
        }
    }

    private static async Task AddMemberHierarchyAsync(
        Solution solution, ISymbol member, bool up, bool down, bool implementations, List<HierarchyEntry> entries, CancellationToken cancellationToken)
    {
        if (up)
        {
            for (var overridden = Overridden(member); overridden is not null; overridden = Overridden(overridden))
            {
                entries.Add(new HierarchyEntry("overrides", overridden));
            }

            if (member.ContainingType is { } containing)
            {
                foreach (var interfaceMember in containing.AllInterfaces.SelectMany(i => i.GetMembers()))
                {
                    if (SymbolEqualityComparer.Default.Equals(containing.FindImplementationForInterfaceMember(interfaceMember), member))
                    {
                        entries.Add(new HierarchyEntry("implements", interfaceMember));
                    }
                }
            }
        }

        if (down)
        {
            var overrides = await SymbolFinder.FindOverridesAsync(member, solution, cancellationToken: cancellationToken);
            entries.AddRange(overrides.Select(o => new HierarchyEntry("overridden by", o)));
        }

        if (implementations && (member.ContainingType?.TypeKind == TypeKind.Interface || member.IsAbstract))
        {
            var implementing = await SymbolFinder.FindImplementationsAsync(member, solution, cancellationToken: cancellationToken);
            entries.AddRange(implementing.Select(i => new HierarchyEntry("implemented by", i)));
        }
    }

    private static ISymbol? Overridden(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.OverriddenMethod,
        IPropertySymbol property => property.OverriddenProperty,
        IEventSymbol @event => @event.OverriddenEvent,
        _ => null,
    };
}
