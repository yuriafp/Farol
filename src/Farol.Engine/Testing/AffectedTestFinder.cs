using System.Collections.Immutable;
using Farol.Engine.Navigation;
using Farol.Engine.Workspaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Farol.Engine.Testing;

/// <summary>The tests that reach some symbols, by test project file. Incomplete when the search hit its size limit.</summary>
public sealed record AffectedTests(IReadOnlyDictionary<string, IReadOnlyList<string>> TestsByProject, bool Complete);

/// <summary>
/// Walks references backwards from the changed symbols — each reference's enclosing member, then that member's
/// references — until it reaches test methods. Only code test projects can call is searched: the test projects and
/// what they depend on. Over-selecting is fine; missing a test that reaches the code is not.
/// </summary>
public static class AffectedTestFinder
{
    private const int MaxSymbols = 3000;

    public static async Task<AffectedTests> FindAsync(
        WorkspaceSnapshot snapshot, IReadOnlyList<TestProjectInfo> testProjects, IEnumerable<SymbolVariant> roots, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(testProjects);
        ArgumentNullException.ThrowIfNull(roots);

        var solution = snapshot.Solution;
        var graph = solution.GetProjectDependencyGraph();
        var testProjectByVariant = testProjects.SelectMany(p => p.Variants.Select(v => (v, p))).ToDictionary(x => x.v, x => x.p);
        var searched = new HashSet<ProjectId>(testProjectByVariant.Keys);
        foreach (var id in testProjectByVariant.Keys)
        {
            searched.UnionWith(graph.GetProjectsThatThisProjectTransitivelyDependsOn(id));
        }

        var documents = searched.Select(solution.GetProject).OfType<Project>().SelectMany(p => p.Documents).ToImmutableHashSet<Document>();
        var tests = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<(string Id, ProjectId? Project)>();
        var queue = new Queue<SymbolVariant>();
        foreach (var root in roots)
        {
            Enqueue(root.Symbol, root.ProjectId);
        }

        var complete = true;
        while (queue.Count > 0)
        {
            if (visited.Count > MaxSymbols)
            {
                complete = false;
                break;
            }

            var (symbol, projectId) = queue.Dequeue();
            if (symbol is IMethodSymbol method && TestFrameworks.IsTest(method))
            {
                AddTest(method, projectId);
                continue;
            }

            foreach (var reference in (await SymbolFinder.FindReferencesAsync(symbol, solution, documents, cancellationToken)).SelectMany(r => r.Locations))
            {
                var model = await reference.Document.GetSemanticModelAsync(cancellationToken);
                if (Enclosing(model?.GetEnclosingSymbol(reference.Location.SourceSpan.Start, cancellationToken)) is not { } caller)
                {
                    continue;
                }

                if (caller is IMethodSymbol test && TestFrameworks.IsTest(test))
                {
                    AddTest(test, reference.Document.Project.Id);
                }
                else
                {
                    Enqueue(caller, reference.Document.Project.Id);
                }
            }
        }

        return new AffectedTests(tests.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)[.. kv.Value], StringComparer.OrdinalIgnoreCase), complete);

        void Enqueue(ISymbol symbol, ProjectId? projectId)
        {
            if (visited.Add((SymbolFormatter.Id(symbol), projectId)))
            {
                queue.Enqueue(new SymbolVariant(symbol, projectId));

                // Callers through an interface or a base class reach this member too.
                foreach (var contract in Contracts(symbol))
                {
                    Enqueue(contract, projectId);
                }
            }
        }

        void AddTest(IMethodSymbol method, ProjectId? projectId)
        {
            if (projectId is not null && testProjectByVariant.TryGetValue(projectId, out var project))
            {
                if (!tests.TryGetValue(project.FilePath, out var names))
                {
                    names = new SortedSet<string>(StringComparer.Ordinal);
                    tests[project.FilePath] = names;
                }

                names.Add(TestFrameworks.FullyQualifiedName(method));
            }
        }
    }

    // The interface members a member implements and the member it overrides.
    private static IEnumerable<ISymbol> Contracts(ISymbol symbol)
    {
        if (symbol is not (IMethodSymbol or IPropertySymbol or IEventSymbol) || symbol.ContainingType is not { } type)
        {
            yield break;
        }

        // Every interface member, not only same-named ones: explicit implementations are named "IFoo.Bar".
        foreach (var member in type.AllInterfaces.SelectMany(i => i.GetMembers()))
        {
            if (SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(member), symbol))
            {
                yield return member;
            }
        }

        var overridden = symbol switch
        {
            IMethodSymbol method => (ISymbol?)method.OverriddenMethod,
            IPropertySymbol property => property.OverriddenProperty,
            IEventSymbol @event => @event.OverriddenEvent,
            _ => null,
        };
        if (overridden is not null)
        {
            yield return overridden;
        }
    }

    // The member whose body holds a reference: lambdas and local functions belong to their method, accessors to their property.
    private static ISymbol? Enclosing(ISymbol? symbol)
    {
        while (symbol is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction })
        {
            symbol = symbol.ContainingSymbol;
        }

        return symbol switch
        {
            IMethodSymbol { AssociatedSymbol: { } property } => property,
            IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol or INamedTypeSymbol => symbol,
            _ => null,
        };
    }
}
