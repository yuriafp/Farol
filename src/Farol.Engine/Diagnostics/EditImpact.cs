using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Farol.Engine.Diagnostics;

/// <summary>How much of the solution an edit to one file can break.</summary>
internal enum ImpactReach
{
    /// <summary>The files in <see cref="EditImpact.Documents"/>.</summary>
    Files,

    /// <summary>Every file of the edited project (a global using changed).</summary>
    Project,

    /// <summary>The edited project and every project that depends on it.</summary>
    Dependents,
}

/// <summary>
/// The files a declaration change in one file can break, found from what changed rather than from the project graph:
/// the files that use a removed or changed declaration, the files that name an added one (overload resolution,
/// hiding, ambiguity), and the implementations an added abstract member requires. Changes whose reach a name cannot
/// bound (type headers, operators, assembly attributes) fall back to the edited project and its dependents.
/// </summary>
internal sealed record EditImpact(ImpactReach Reach, IReadOnlySet<DocumentId> Documents)
{
    // Beyond this many changed declarations in one file (a rewrite), compiling the dependents is the honest answer.
    private const int MaxChangedDeclarations = 64;

    private static readonly ConditionalWeakTable<SyntaxTree, CachedImpact> Cache = new();

    public static EditImpact Dependents { get; } = new(ImpactReach.Dependents, new HashSet<DocumentId>());

    /// <summary>
    /// The impact of the change from <paramref name="loaded"/>'s version of the document to <paramref name="current"/>'s.
    /// Either side may lack it (a file added or removed since the load).
    /// </summary>
    public static async Task<EditImpact> OfAsync(Solution loaded, Solution current, DocumentId id, CancellationToken cancellationToken)
    {
        var before = loaded.GetDocument(id);
        var after = current.GetDocument(id);
        var oldTree = before is null ? null : await before.GetSyntaxTreeAsync(cancellationToken);
        var newTree = after is null ? null : await after.GetSyntaxTreeAsync(cancellationToken);
        var key = newTree ?? oldTree;
        if (key is not null && Cache.TryGetValue(key, out var cached) && ReferenceEquals(cached.Loaded, loaded))
        {
            return cached.Impact;
        }

        var impact = await AnalyzeAsync(loaded, current, id, before, after, oldTree, newTree, cancellationToken);
        if (key is not null)
        {
            Cache.AddOrUpdate(key, new CachedImpact(loaded, impact));
        }

        return impact;
    }

    private static async Task<EditImpact> AnalyzeAsync(
        Solution loaded, Solution current, DocumentId id, Document? before, Document? after, SyntaxTree? oldTree, SyntaxTree? newTree, CancellationToken cancellationToken)
    {
        var documents = new HashSet<DocumentId>();
        if (after is not null)
        {
            documents.Add(id);
        }

        // Mid-edit syntax errors make every later declaration look removed: report the file's own errors first.
        if (newTree is not null && newTree.GetDiagnostics(cancellationToken).Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            return new EditImpact(ImpactReach.Files, documents);
        }

        var old = oldTree is null ? [] : DeclarationShape.Of(await oldTree.GetRootAsync(cancellationToken));
        var updated = newTree is null ? [] : DeclarationShape.Of(await newTree.GetRootAsync(cancellationToken));
        var oldKeys = old.Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
        var newKeys = updated.Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
        var removed = old.Where(d => !newKeys.Contains(d.Key)).ToList();
        var added = updated.Where(d => !oldKeys.Contains(d.Key)).ToList();
        var changed = removed.Concat(added).ToList();

        if (changed.Count > MaxChangedDeclarations
            || changed.Any(d => d.Kind == DeclarationKind.Unknown)
            || added.Any(d => d.Kind == DeclarationKind.UnnamedMember)
            || TypeHeaderChanged(removed, added))
        {
            return Dependents;
        }

        if (changed.Any(d => d.Kind == DeclarationKind.ProjectWide))
        {
            return new EditImpact(ImpactReach.Project, documents);
        }

        foreach (var declaration in removed.Where(d => d.Kind is DeclarationKind.Type or DeclarationKind.Member or DeclarationKind.UnnamedMember))
        {
            if (!await AddUsesAsync(loaded, before!, declaration, documents, cancellationToken))
            {
                return Dependents;
            }
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var declaration in added.Where(d => d.Kind is DeclarationKind.Type or DeclarationKind.Member))
        {
            names.Add(SimpleName(declaration.Name));
            if (!await AddRequiredImplementationsAsync(loaded, current, after!, declaration, documents, cancellationToken))
            {
                return Dependents;
            }
        }

        if (names.Count > 0 && after is not null)
        {
            await AddFilesNamingAsync(current, after.Project.Id, names, documents, cancellationToken);
        }

        documents.RemoveWhere(d => current.GetDocument(d) is null);
        return new EditImpact(ImpactReach.Files, documents);
    }

    // A type whose header changed (base types, type parameters, modifiers...) changes what every use of it binds to.
    private static bool TypeHeaderChanged(List<Declaration> removed, List<Declaration> added) =>
        removed.Where(d => d.Kind == DeclarationKind.Type)
            .Any(r => added.Any(a => a.Kind == DeclarationKind.Type && a.Container == r.Container && a.Name == r.Name));

    /// <summary>The files that use a declaration that is gone or changed: its references, overrides and implementations.</summary>
    private static async Task<bool> AddUsesAsync(Solution loaded, Document before, Declaration declaration, HashSet<DocumentId> documents, CancellationToken cancellationToken)
    {
        var model = await before.GetSemanticModelAsync(cancellationToken);
        if (model?.GetDeclaredSymbol(declaration.Node, cancellationToken) is not { } symbol)
        {
            return false;
        }

        foreach (var referenced in await SymbolFinder.FindReferencesAsync(symbol, loaded, cancellationToken))
        {
            foreach (var location in referenced.Locations)
            {
                documents.Add(location.Document.Id);
            }

            AddDeclaringDocuments(loaded, referenced.Definition, documents);
        }

        if (symbol.IsVirtual || symbol.IsAbstract || symbol.IsOverride || symbol.ContainingType?.TypeKind == TypeKind.Interface)
        {
            foreach (var overriding in await SymbolFinder.FindOverridesAsync(symbol, loaded, cancellationToken: cancellationToken))
            {
                AddDeclaringDocuments(loaded, overriding, documents);
            }

            foreach (var implementation in await SymbolFinder.FindImplementationsAsync(symbol, loaded, cancellationToken: cancellationToken))
            {
                AddDeclaringDocuments(loaded, implementation, documents);
            }
        }

        return true;
    }

    /// <summary>
    /// An abstract member added to an interface or abstract class must be implemented by every type below it; any added
    /// member can also clash with the type's other partial declarations.
    /// </summary>
    private static async Task<bool> AddRequiredImplementationsAsync(
        Solution loaded, Solution current, Document after, Declaration declaration, HashSet<DocumentId> documents, CancellationToken cancellationToken)
    {
        var model = await after.GetSemanticModelAsync(cancellationToken);
        if (model?.GetDeclaredSymbol(declaration.Node, cancellationToken) is not { } symbol)
        {
            return false;
        }

        if (symbol.ContainingType is not { } type)
        {
            return true;
        }

        AddDeclaringDocuments(current, type, documents);
        if (!symbol.IsAbstract)
        {
            return true;
        }

        // The types below it are looked up in the load solution, whose compilations are built: in the current one, every
        // dependent project would have to be compiled again first. Types declared in files changed since the load are
        // checked anyway, and a type added since the load has nothing below it elsewhere.
        if (await AtLoadAsync(loaded, after.Project.Id, type, cancellationToken) is not { } original)
        {
            return true;
        }

        var below = original.TypeKind == TypeKind.Interface
            ? await SymbolFinder.FindImplementationsAsync(original, loaded, cancellationToken: cancellationToken)
            : await SymbolFinder.FindDerivedClassesAsync(original, loaded, transitive: true, cancellationToken: cancellationToken);
        foreach (var derived in below)
        {
            AddDeclaringDocuments(loaded, derived, documents);
        }

        return true;
    }

    private static async Task<INamedTypeSymbol?> AtLoadAsync(Solution loaded, ProjectId project, INamedTypeSymbol type, CancellationToken cancellationToken)
    {
        var compilation = loaded.GetProject(project) is { } atLoad ? await atLoad.GetCompilationAsync(cancellationToken) : null;
        return compilation is not null && DocumentationCommentId.CreateDeclarationId(type) is { } id
            ? DocumentationCommentId.GetFirstSymbolForDeclarationId(id, compilation) as INamedTypeSymbol
            : null;
    }

    /// <summary>
    /// The files of the edited project and of the projects that can see it whose text contains one of the added names:
    /// only they can bind differently (an overload chosen, a name hidden or made ambiguous). VB names ignore case.
    /// </summary>
    private static async Task AddFilesNamingAsync(Solution current, ProjectId edited, HashSet<string> names, HashSet<DocumentId> documents, CancellationToken cancellationToken)
    {
        var projects = current.GetProjectDependencyGraph().GetProjectsThatTransitivelyDependOnThisProject(edited).Append(edited);
        var candidates = projects.Select(current.GetProject).OfType<Project>().SelectMany(p => p.Documents).Where(d => !documents.Contains(d.Id)).ToList();
        var found = new System.Collections.Concurrent.ConcurrentBag<DocumentId>();
        await Parallel.ForEachAsync(candidates, cancellationToken, async (document, token) =>
        {
            var text = (await document.GetTextAsync(token)).ToString();
            var comparison = document.Project.Language == LanguageNames.VisualBasic ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (names.Any(name => ContainsIdentifier(text, name, comparison)))
            {
                found.Add(document.Id);
            }
        });

        documents.UnionWith(found);
    }

    internal static bool ContainsIdentifier(string text, string name, StringComparison comparison)
    {
        for (var at = text.IndexOf(name, comparison); at >= 0; at = text.IndexOf(name, at + 1, comparison))
        {
            var end = at + name.Length;
            if ((at == 0 || !IsIdentifierPart(text[at - 1])) && (end == text.Length || !IsIdentifierPart(text[end])))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static void AddDeclaringDocuments(Solution solution, ISymbol symbol, HashSet<DocumentId> documents)
    {
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            if (solution.GetDocumentId(reference.SyntaxTree) is { } id)
            {
                documents.Add(id);
            }
        }
    }

    private static string SimpleName(string name)
    {
        var arity = name.IndexOf('`', StringComparison.Ordinal);
        return arity >= 0 ? name[..arity] : name;
    }

    private sealed record CachedImpact(Solution Loaded, EditImpact Impact);
}
