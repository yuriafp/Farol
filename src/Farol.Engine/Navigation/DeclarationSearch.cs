using Farol.Core;
using Farol.Engine.Workspaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Farol.Engine.Navigation;

/// <summary>Finds declarations by name the way IDE "go to symbol" does: exact, prefix, substring and camel-case humps.</summary>
public static class DeclarationSearch
{
    public static readonly IReadOnlyList<string> Kinds =
        ["any", "type", "class", "interface", "struct", "enum", "record", "delegate", "method", "property", "field", "event"];

    public static async Task<IReadOnlyList<SymbolCandidate>> SearchAsync(
        WorkspaceSnapshot snapshot, string query, string kind, string? project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        kind = kind.Trim().ToUpperInvariant();
        if (!Kinds.Contains(kind, StringComparer.OrdinalIgnoreCase))
        {
            throw new FarolException(ErrorCodes.InvalidArgument, $"Unknown kind '{kind}'.", $"Use one of: {string.Join(", ", Kinds)}.");
        }

        var found = await SymbolFinder.FindSourceDeclarationsWithPatternAsync(snapshot.Solution, query.Trim(), SymbolFilter.TypeAndMember, cancellationToken);
        var matching = found.Where(s => IsNavigable(s) && MatchesKind(s, kind) && MatchesProject(snapshot, s, project));

        var candidates = await SymbolLocator.GroupAsync(snapshot, matching, cancellationToken);
        return [.. candidates
            .OrderBy(c => Rank(c.Symbol.Name, query.Trim()))
            .ThenBy(c => c.Symbol.Name.Length)
            .ThenBy(c => c.Id, StringComparer.Ordinal)];
    }

    private static bool IsNavigable(ISymbol symbol) =>
        !symbol.IsImplicitlyDeclared
        && symbol is not IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise };

    private static bool MatchesKind(ISymbol symbol, string kind) => kind switch
    {
        "ANY" => true,
        "TYPE" => symbol is INamedTypeSymbol,
        "RECORD" => symbol is INamedTypeSymbol { IsRecord: true },
        "CLASS" => symbol is INamedTypeSymbol { TypeKind: TypeKind.Class, IsRecord: false },
        "INTERFACE" => symbol is INamedTypeSymbol { TypeKind: TypeKind.Interface },
        "STRUCT" => symbol is INamedTypeSymbol { TypeKind: TypeKind.Struct },
        "ENUM" => symbol is INamedTypeSymbol { TypeKind: TypeKind.Enum },
        "DELEGATE" => symbol is INamedTypeSymbol { TypeKind: TypeKind.Delegate },
        "METHOD" => symbol is IMethodSymbol,
        "PROPERTY" => symbol is IPropertySymbol,
        "FIELD" => symbol is IFieldSymbol,
        "EVENT" => symbol is IEventSymbol,
        _ => false,
    };

    private static bool MatchesProject(WorkspaceSnapshot snapshot, ISymbol symbol, string? project)
    {
        if (string.IsNullOrWhiteSpace(project))
        {
            return true;
        }

        var id = symbol.ContainingAssembly is { } assembly ? snapshot.Solution.GetProject(assembly)?.Id : null;
        return string.Equals(snapshot.Projects.Get(id)?.Name, project.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static int Rank(string name, string query)
    {
        if (string.Equals(name, query, StringComparison.Ordinal))
        {
            return 0;
        }

        if (string.Equals(name, query, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return name.Contains(query, StringComparison.OrdinalIgnoreCase) ? 3 : 4;
    }
}
