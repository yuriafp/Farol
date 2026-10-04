using System.Collections.Concurrent;
using Farol.Core;
using Farol.Engine.Workspaces;
using Microsoft.CodeAnalysis;

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

        // Each compilation's declaration table, scanned in parallel: names are matched before any symbol is created, so a
        // query over a large solution costs milliseconds once the compilations exist.
        var pattern = NamePattern.Create(query);
        var found = new ConcurrentBag<ISymbol>();
        var projects = snapshot.Solution.Projects.Where(p => MatchesProject(snapshot, p.Id, project));
        await Parallel.ForEachAsync(projects, cancellationToken, async (candidate, token) =>
        {
            var compilation = await candidate.GetCompilationAsync(token);
            if (compilation is null)
            {
                return;
            }

            foreach (var symbol in compilation.GetSymbolsWithName(pattern.MatchesName, SymbolFilter.TypeAndMember, token))
            {
                if (IsNavigable(symbol) && MatchesKind(symbol, kind) && pattern.MatchesContainers(symbol))
                {
                    found.Add(symbol);
                }
            }
        });

        var candidates = await SymbolLocator.GroupAsync(snapshot, found, cancellationToken);
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

    private static bool MatchesProject(WorkspaceSnapshot snapshot, ProjectId id, string? project) =>
        string.IsNullOrWhiteSpace(project) || string.Equals(snapshot.Projects.Get(id)?.Name, project.Trim(), StringComparison.OrdinalIgnoreCase);

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
