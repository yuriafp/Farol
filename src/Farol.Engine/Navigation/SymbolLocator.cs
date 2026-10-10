using System.Text.RegularExpressions;
using Farol.Core;
using Farol.Engine.Workspaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Farol.Engine.Navigation;

/// <summary>One declaration of a symbol in one Roslyn project (a multi-targeted project yields one per target framework).</summary>
public sealed record SymbolVariant(ISymbol Symbol, ProjectId? ProjectId);

/// <summary>A distinct symbol, identified by its documentation comment ID, with every target-framework variant.</summary>
public sealed record SymbolCandidate(string Id, IReadOnlyList<SymbolVariant> Variants)
{
    public ISymbol Symbol => Variants[0].Symbol;
}

/// <summary>
/// Resolves how agents refer to symbols — a name, a dotted name, a documentation comment ID or
/// "path:line[:column]" — into candidates. Several candidates are an answer, not an error.
/// </summary>
public static partial class SymbolLocator
{
    /// <summary>Resolves a reference whose "path:line" form, if it has one, is relative to <paramref name="rootDirectory"/>.</summary>
    public static Task<IReadOnlyList<SymbolCandidate>> ResolveAsync(
        WorkspaceSnapshot snapshot, string rootDirectory, string reference, CancellationToken cancellationToken) =>
        ResolveAsync(snapshot, path => Path.GetFullPath(path, rootDirectory), reference, cancellationToken);

    /// <summary>
    /// Resolves a reference. <paramref name="resolvePath"/> turns the path of the "path:line" form into a full path,
    /// and refuses it, before anything is read, when the caller may not read it.
    /// </summary>
    public static async Task<IReadOnlyList<SymbolCandidate>> ResolveAsync(
        WorkspaceSnapshot snapshot, Func<string, string> resolvePath, string reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(resolvePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        var text = reference.Trim();
        IReadOnlyList<ISymbol> symbols;
        if (DocumentationId().IsMatch(text))
        {
            symbols = await FromDocumentationIdAsync(snapshot.Solution, SymbolFormatter.NormalizeId(text), cancellationToken);
        }
        else if (TryParseLocation(text, out var path, out var line, out var column))
        {
            symbols = await FromLocationAsync(snapshot, resolvePath(path), path, line, column, cancellationToken);
        }
        else
        {
            symbols = await FromNameAsync(snapshot.Solution, text, cancellationToken);
        }

        return await GroupAsync(snapshot, symbols, cancellationToken);
    }

    /// <summary>Recognizes the "path:line[:column]" form.</summary>
    private static bool TryParseLocation(string reference, out string path, out int line, out int? column)
    {
        if (Location().Match(reference) is { Success: true } match)
        {
            path = match.Groups["path"].Value;
            line = int.Parse(match.Groups["line"].ValueSpan, System.Globalization.CultureInfo.InvariantCulture);
            column = match.Groups["column"].Success ? int.Parse(match.Groups["column"].ValueSpan, System.Globalization.CultureInfo.InvariantCulture) : null;
            return true;
        }

        path = string.Empty;
        line = 0;
        column = null;
        return false;
    }

    /// <summary>Groups symbols by ID and adds the variants from other target frameworks of the same project files.</summary>
    public static async Task<IReadOnlyList<SymbolCandidate>> GroupAsync(WorkspaceSnapshot snapshot, IEnumerable<ISymbol> symbols, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var solution = snapshot.Solution;
        var candidates = new List<SymbolCandidate>();
        foreach (var group in symbols.GroupBy(SymbolFormatter.Id, StringComparer.Ordinal))
        {
            var variants = new List<SymbolVariant>();
            foreach (var symbol in group)
            {
                variants.Add(new SymbolVariant(symbol, ProjectOf(solution, symbol)));
            }

            await AddSiblingVariantsAsync(snapshot, group.Key, variants, cancellationToken);
            candidates.Add(new SymbolCandidate(group.Key, variants.DistinctBy(v => v.ProjectId).ToList()));
        }

        return candidates;
    }

    private static async Task<IReadOnlyList<ISymbol>> FromNameAsync(Solution solution, string text, CancellationToken cancellationToken)
    {
        // "Type.Member(int)" → name "Member", qualifier ["Type"]; parameter lists are ignored (overloads come back as candidates).
        var withoutParameters = text.Split('(')[0];
        var segments = withoutParameters.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return [];
        }

        var name = segments[^1];
        var qualifier = segments[..^1];
        bool Matches(ISymbol symbol) => qualifier.Length == 0 || EndsWithQualifier(symbol, qualifier);

        var found = (await SymbolFinder.FindSourceDeclarationsAsync(solution, name, ignoreCase: false, SymbolFilter.TypeAndMember, cancellationToken)).Where(Matches).ToList();
        if (found.Count == 0)
        {
            found = [.. (await SymbolFinder.FindSourceDeclarationsAsync(solution, name, ignoreCase: true, SymbolFilter.TypeAndMember, cancellationToken)).Where(Matches)];
        }

        return found.Count > 0 ? found : await FromReferencesAsync(solution, name, qualifier, cancellationToken);
    }

    /// <summary>
    /// Types and members of packages and the framework, which no source declares, exact case first. The declaration index
    /// of a project's references holds types only, so a member is looked up in the types its qualifier names. One symbol
    /// per ID: projects that target different frameworks reference different builds of an assembly, and the first one
    /// found stands for them all.
    /// </summary>
    private static async Task<IReadOnlyList<ISymbol>> FromReferencesAsync(Solution solution, string name, string[] qualifier, CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, ISymbol>(StringComparer.Ordinal);
        foreach (var project in solution.Projects)
        {
            foreach (var type in await ReferencedTypesAsync(project, name, qualifier, cancellationToken))
            {
                found.TryAdd(SymbolFormatter.Id(type), type);
            }

            if (qualifier.Length == 0)
            {
                continue;
            }

            foreach (var type in await ReferencedTypesAsync(project, qualifier[^1], qualifier[..^1], cancellationToken))
            {
                foreach (var member in type.GetMembers().Where(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    found.TryAdd(SymbolFormatter.Id(member), member);
                }
            }
        }

        var exact = found.Values.Where(s => s.Name.Equals(name, StringComparison.Ordinal)).ToList();
        return exact.Count > 0 ? exact : [.. found.Values];
    }

    private static async Task<IEnumerable<INamedTypeSymbol>> ReferencedTypesAsync(Project project, string name, string[] qualifier, CancellationToken cancellationToken) =>
        (await SymbolFinder.FindDeclarationsAsync(project, name, ignoreCase: true, SymbolFilter.Type, cancellationToken))
            .OfType<INamedTypeSymbol>()
            .Where(t => t.Locations.Any(l => l.IsInMetadata) && EndsWithQualifier(t, qualifier));

    private static bool EndsWithQualifier(ISymbol symbol, string[] qualifier)
    {
        var containers = new List<string>();
        for (var container = symbol.ContainingSymbol; container is not null and not INamespaceSymbol { IsGlobalNamespace: true }; container = container.ContainingSymbol)
        {
            containers.Insert(0, container.Name);
        }

        return containers.Count >= qualifier.Length
            && containers.Skip(containers.Count - qualifier.Length).SequenceEqual(qualifier, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<IReadOnlyList<ISymbol>> FromDocumentationIdAsync(Solution solution, string id, CancellationToken cancellationToken)
    {
        // Fast path: the declaration index finds the name without compiling every project.
        var name = SimpleNameFromDocumentationId(id);
        var indexed = (await SymbolFinder.FindSourceDeclarationsAsync(solution, name, ignoreCase: false, SymbolFilter.TypeAndMember, cancellationToken))
            .Where(s => SymbolFormatter.Id(s) == id)
            .ToList();
        if (indexed.Count > 0)
        {
            return indexed;
        }

        // Constructors, operators and external (metadata) symbols: resolve against each compilation.
        var external = new List<ISymbol>();
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null)
            {
                continue;
            }

            foreach (var symbol in DocumentationCommentId.GetSymbolsForDeclarationId(id, compilation))
            {
                if (SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, compilation.Assembly))
                {
                    indexed.Add(symbol);
                }
                else if (external.Count == 0)
                {
                    external.Add(symbol);
                }
            }
        }

        return indexed.Count > 0 ? indexed : external;
    }

    private static async Task<IReadOnlyList<ISymbol>> FromLocationAsync(
        WorkspaceSnapshot snapshot, string fullPath, string path, int line, int? column, CancellationToken cancellationToken)
    {
        var document = snapshot.Solution.GetDocument(SourceDocuments.Find(snapshot, fullPath, path)[0])!;
        var text = await document.GetTextAsync(cancellationToken);
        if (line < 1 || line > text.Lines.Count)
        {
            throw new FarolException(ErrorCodes.InvalidArgument, $"Line {line} is outside '{path}' ({text.Lines.Count} lines).");
        }

        var span = text.Lines[line - 1].Span;
        if (column is { } c)
        {
            var position = span.Start + Math.Clamp(c - 1, 0, span.Length);
            return await SymbolFinder.FindSymbolAtPositionAsync(document, position, cancellationToken) is { } atColumn ? [atColumn] : [];
        }

        // No column: prefer what the line declares; otherwise the first meaningful symbol it references.
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        var declared = root!.DescendantNodes(span)
            .Where(n => span.Contains(n.SpanStart))
            .Select(n => model!.GetDeclaredSymbol(n, cancellationToken))
            .OfType<ISymbol>()
            .FirstOrDefault(s => s is INamedTypeSymbol or IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol);
        if (declared is not null)
        {
            return [declared];
        }

        foreach (var token in root.DescendantTokens(span).Where(t => t.Text.Length > 0 && (char.IsLetter(t.Text[0]) || t.Text[0] == '_')))
        {
            var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, token.SpanStart, cancellationToken);
            if (symbol is not null and not ITypeSymbol { SpecialType: not SpecialType.None } and not INamespaceSymbol)
            {
                return [symbol];
            }
        }

        return [];
    }

    private static async Task AddSiblingVariantsAsync(WorkspaceSnapshot snapshot, string id, List<SymbolVariant> variants, CancellationToken cancellationToken)
    {
        var solution = snapshot.Solution;
        var known = variants.Select(v => v.ProjectId).OfType<ProjectId>().ToHashSet();
        var files = known.Select(p => snapshot.Projects.Get(p)?.FilePath).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var project in solution.Projects.Where(p => p.FilePath is not null && files.Contains(p.FilePath) && !known.Contains(p.Id)))
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            var sibling = compilation is null
                ? null
                : DocumentationCommentId.GetSymbolsForDeclarationId(id, compilation)
                    .FirstOrDefault(s => SymbolEqualityComparer.Default.Equals(s.ContainingAssembly, compilation.Assembly));
            if (sibling is not null)
            {
                variants.Add(new SymbolVariant(sibling, project.Id));
            }
        }
    }

    private static ProjectId? ProjectOf(Solution solution, ISymbol symbol) =>
        symbol.ContainingAssembly is { } assembly ? solution.GetProject(assembly)?.Id : null;

    private static string SimpleNameFromDocumentationId(string id)
    {
        var body = id[2..].Split('(')[0];
        var name = body[(body.LastIndexOf('.') + 1)..];
        var arity = name.IndexOf('`', StringComparison.Ordinal);
        return arity >= 0 ? name[..arity] : name;
    }

    // "F:\src\A.cs:12" is a path on drive F, not the ID of a field: no ID has a slash right after the colon.
    [GeneratedRegex(@"^[TMPFEN]:(?![\\/])\S+$")]
    private static partial Regex DocumentationId();

    [GeneratedRegex(@"^(?<path>.+?\.(cs|vb)):(?<line>\d+)(:(?<column>\d+))?$", RegexOptions.IgnoreCase)]
    private static partial Regex Location();
}
