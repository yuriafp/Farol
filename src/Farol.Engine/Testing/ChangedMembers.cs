using Farol.Engine.Navigation;
using Farol.Engine.Workspaces;
using Microsoft.CodeAnalysis;

namespace Farol.Engine.Testing;

/// <summary>The members edited since the workspace loaded: the starting points for "the tests my changes affect".</summary>
public static class ChangedMembers
{
    public static async Task<IReadOnlyList<SymbolVariant>> SinceLoadAsync(WorkspaceSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var members = new List<SymbolVariant>();
        var seen = new HashSet<(string, ProjectId)>();
        foreach (var project in snapshot.Solution.GetChanges(snapshot.Load.Solution).GetProjectChanges())
        {
            foreach (var id in project.GetChangedDocuments(onlyGetDocumentsWithTextChanges: true).Concat(project.GetAddedDocuments()))
            {
                var document = snapshot.Solution.GetDocument(id)!;
                if (await document.GetSyntaxTreeAsync(cancellationToken) is not { } tree || await document.GetSemanticModelAsync(cancellationToken) is not { } model)
                {
                    continue;
                }

                var root = await tree.GetRootAsync(cancellationToken);
                var before = snapshot.Load.Solution.GetDocument(id) is { } old ? await old.GetSyntaxTreeAsync(cancellationToken) : null;
                foreach (var span in before is null ? [root.FullSpan] : tree.GetChangedSpans(before))
                {
                    // The nodes crossing the change run from the file down to it: the members they declare are what changed.
                    var declared = root.DescendantNodesAndSelf(span)
                        .Select(node => model.GetDeclaredSymbol(node, cancellationToken))
                        .OfType<ISymbol>()
                        .ToList();
                    var changed = declared.Where(IsMember).Select(s => s is IMethodSymbol { AssociatedSymbol: { } property } ? property : s).ToList();
                    foreach (var member in changed.Count > 0 ? changed : [.. declared.OfType<INamedTypeSymbol>().TakeLast(1)])
                    {
                        if (seen.Add((SymbolFormatter.Id(member), id.ProjectId)))
                        {
                            members.Add(new SymbolVariant(member, id.ProjectId));
                        }
                    }
                }
            }
        }

        return members;
    }

    private static bool IsMember(ISymbol symbol) =>
        symbol is IPropertySymbol or IEventSymbol or IFieldSymbol
            or IMethodSymbol { MethodKind: not (MethodKind.AnonymousFunction or MethodKind.LocalFunction) };
}
