using Farol.Engine.Markup;
using Farol.Engine.Workspaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;
using CSharpSyntax = Microsoft.CodeAnalysis.CSharp.Syntax;
using VisualBasicSyntax = Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Farol.Engine.Navigation;

/// <summary>
/// One place in source. Found in several target frameworks, it is still one hit that lists them. <paramref name="Via"/>
/// names the type of the interface or base member a use goes through, when it is not a use of the symbol itself.
/// </summary>
public sealed record SourceHit(string FilePath, int Line, int Column, string Snippet, string Project, IReadOnlyList<string> TargetFrameworks, string Kind, string? Via = null);

/// <summary>The symbol's definitions and uses, and the members (see <see cref="ReferenceFinder.ThroughAsync"/>) some uses go through.</summary>
public sealed record ReferenceResult(IReadOnlyList<SourceHit> Definitions, IReadOnlyList<SourceHit> References, IReadOnlyList<ISymbol> Through);

/// <summary>
/// Compiler-accurate references for one symbol across every target framework, classified through
/// <see cref="IOperation"/> (call, new, read, write…) so the same code serves C# and VB, plus the references
/// the compiler never sees: WebForms, ASMX/WCF/handler and XAML markup.
/// </summary>
public static class ReferenceFinder
{
    public const int MaxSnippetLength = 160;

    public static readonly IReadOnlyList<string> Kinds = ["call", "new", "read", "write", "method group", "reference", "markup"];

    public static async Task<ReferenceResult> FindAsync(WorkspaceSnapshot snapshot, SymbolCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(candidate);

        var definitions = new SourceHitCollector(snapshot);
        var references = new SourceHitCollector(snapshot);
        var uses = new List<(ReferenceLocation Location, ISymbol Symbol, string? Via)>();
        var through = new Dictionary<string, ISymbol>(StringComparer.Ordinal);
        foreach (var variant in candidate.Variants)
        {
            var (found, members) = await SearchAsync(variant.Symbol, snapshot.Solution, cancellationToken);
            foreach (var location in found.Where(r => SymbolFormatter.Id(r.Definition) == candidate.Id).SelectMany(r => r.Definition.Locations).Where(l => l.IsInSource))
            {
                if (snapshot.Solution.GetDocument(location.SourceTree) is { } document)
                {
                    await definitions.AddAsync(document, location, "definition", via: null, cancellationToken);
                }
            }

            uses.AddRange(UsesOf(found, candidate.Id, members));
            foreach (var member in members)
            {
                through.TryAdd(SymbolFormatter.Id(member), member);
            }
        }

        // Binding the code around every use to classify it cost as much as the search itself, so the syntax decides when
        // it can (types, calls); the rest binds with one semantic model per document, documents in parallel.
        var kinds = new string[uses.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, uses.Count).GroupBy(i => uses[i].Location.Document.Id),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken },
            async (inDocument, token) =>
            {
                var document = uses[inDocument.First()].Location.Document;
                var root = await document.GetSyntaxRootAsync(token);
                SemanticModel? model = null;
                foreach (var index in inDocument)
                {
                    var node = root!.FindNode(uses[index].Location.Location.SourceSpan, getInnermostNodeForTie: true);
                    kinds[index] = KindFromSyntax(uses[index].Symbol, node)
                        ?? Classify(node, model ??= (await document.GetSemanticModelAsync(token))!, token);
                }
            });
        for (var index = 0; index < uses.Count; index++)
        {
            await references.AddAsync(uses[index].Location.Document, uses[index].Location.Location, kinds[index], uses[index].Via, cancellationToken);
        }

        var markup = await MarkupIndex.GetAsync(snapshot, cancellationToken);
        foreach (var reference in markup.ReferencesTo(candidate.Id))
        {
            references.AddMarkup(reference);
        }

        return new ReferenceResult(definitions.ToList(), references.ToList(), [.. through.Values]);
    }

    /// <summary>
    /// Roslyn's references to the symbol, which cascade to related members, and the members a call reaches the symbol
    /// through, whose references are searched on their own when the cascade left them out.
    /// </summary>
    internal static async Task<(List<ReferencedSymbol> Found, IReadOnlyList<ISymbol> Through)> SearchAsync(
        ISymbol symbol, Solution solution, CancellationToken cancellationToken)
    {
        var found = (await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken)).ToList();
        var through = await ThroughAsync(symbol, solution, cancellationToken);
        foreach (var member in through)
        {
            var id = SymbolFormatter.Id(member);
            if (!found.Exists(r => SymbolFormatter.Id(r.Definition) == id))
            {
                found.AddRange((await SymbolFinder.FindReferencesAsync(member, solution, cancellationToken)).Where(r => SymbolFormatter.Id(r.Definition) == id));
            }
        }

        return (found, through);
    }

    /// <summary>
    /// The members declared in the solution that a call reaches the symbol through: the interface members it implements
    /// and the members it overrides, and theirs in turn. Members of referenced assemblies (<c>object.ToString</c>,
    /// <c>IDisposable.Dispose</c>) are left out, since their callers are everywhere.
    /// </summary>
    public static async Task<IReadOnlyList<ISymbol>> ThroughAsync(ISymbol symbol, Solution solution, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        var through = new List<ISymbol>();
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default) { symbol.OriginalDefinition };
        var pending = new Queue<ISymbol>([symbol.OriginalDefinition]);
        while (pending.TryDequeue(out var member))
        {
            var implemented = await SymbolFinder.FindImplementedInterfaceMembersAsync(member, solution, cancellationToken: cancellationToken);
            foreach (var next in implemented.Append(Overridden(member)).OfType<ISymbol>().Select(s => s.OriginalDefinition))
            {
                if (seen.Add(next))
                {
                    pending.Enqueue(next);
                    if (next.Locations.Any(l => l.IsInSource))
                    {
                        through.Add(next);
                    }
                }
            }
        }

        return through;
    }

    /// <summary>How a use through <paramref name="member"/> is marked: the type it goes through.</summary>
    public static string Via(ISymbol member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return member.ContainingType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
    }

    /// <summary>
    /// The uses to report, each with the symbol it refers to: the symbol's own; for a type its constructors', which are
    /// where the type is created (Roslyn files them there); and those through the members in <paramref name="through"/>,
    /// marked with the type they go through. Other cascaded results (the member's other implementations and overrides)
    /// belong to dotnet_hierarchy.
    /// </summary>
    internal static IEnumerable<(ReferenceLocation Location, ISymbol Symbol, string? Via)> UsesOf(
        IEnumerable<ReferencedSymbol> found, string id, IEnumerable<ISymbol>? through = null)
    {
        var via = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var member in through ?? [])
        {
            via.TryAdd(SymbolFormatter.Id(member), Via(member));
        }

        return found
            .Select(r => (Group: r, Id: SymbolFormatter.Id(r.Definition)))
            .Where(r => r.Id == id || IsConstructorOf(r.Group.Definition, id) || via.ContainsKey(r.Id))
            .SelectMany(r => r.Group.Locations
                .Where(l => !l.IsImplicit && l.Location.IsInSource)
                .Select(l => (l, r.Group.Definition, r.Id == id ? null : via.GetValueOrDefault(r.Id))));
    }

    private static ISymbol? Overridden(ISymbol member) => member switch
    {
        IMethodSymbol method => method.OverriddenMethod,
        IPropertySymbol property => property.OverriddenProperty,
        IEventSymbol @event => @event.OverriddenEvent,
        _ => null,
    };

    private static bool IsConstructorOf(ISymbol symbol, string typeId) =>
        symbol is IMethodSymbol { MethodKind: MethodKind.Constructor, ContainingType: { } type } && SymbolFormatter.Id(type) == typeId;

    /// <summary>
    /// The kind the syntax alone tells, without binding: a type is either created or referenced (as a receiver or a type
    /// argument it is still a reference, never the member's read or call), and a method whose name is invoked is called.
    /// </summary>
    internal static string? KindFromSyntax(ISymbol definition, SyntaxNode node) => definition switch
    {
        INamedTypeSymbol => IsCreated(node) ? "new" : "reference",
        IMethodSymbol { MethodKind: MethodKind.Constructor } when IsCreated(node) => "new",
        IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.LocalFunction } when IsInvoked(node) => "call",
        _ => null,
    };

    /// <summary>Whether a type name is the type of a <c>new</c> expression (C# and VB), qualified or not.</summary>
    private static bool IsCreated(SyntaxNode node)
    {
        var type = node;
        while ((type.Parent is CSharpSyntax.QualifiedNameSyntax { Right: var right } && right == type)
            || (type.Parent is CSharpSyntax.AliasQualifiedNameSyntax { Name: var name } && name == type)
            || (type.Parent is VisualBasicSyntax.QualifiedNameSyntax { Right: var vbRight } && vbRight == type))
        {
            type = type.Parent;
        }

        return (type.Parent is CSharpSyntax.ObjectCreationExpressionSyntax { Type: var created } && created == type)
            || (type.Parent is VisualBasicSyntax.ObjectCreationExpressionSyntax { Type: var vbCreated } && vbCreated == type);
    }

    /// <summary>Whether a method name is invoked: <c>M()</c>, <c>x.M()</c>, <c>x?.M()</c> (C# and VB).</summary>
    private static bool IsInvoked(SyntaxNode node)
    {
        SyntaxNode expression = node.Parent switch
        {
            CSharpSyntax.MemberAccessExpressionSyntax access when access.Name == node => access,
            CSharpSyntax.MemberBindingExpressionSyntax binding when binding.Name == node => binding,
            VisualBasicSyntax.MemberAccessExpressionSyntax access when access.Name == node => access,
            _ => node,
        };
        return (expression.Parent is CSharpSyntax.InvocationExpressionSyntax { Expression: var invoked } && invoked == expression)
            || (expression.Parent is VisualBasicSyntax.InvocationExpressionSyntax { Expression: var vbInvoked } && vbInvoked == expression);
    }

    internal static string Classify(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        var depth = 0;
        for (var current = node; current is not null && depth < 4; current = current.Parent, depth++)
        {
            // A use in a declaration (a return or parameter type, a base type) is a plain reference; the declaration's
            // operation would be the whole method body, bound for nothing.
            if (model.GetDeclaredSymbol(current, cancellationToken) is not null)
            {
                break;
            }

            switch (model.GetOperation(current, cancellationToken))
            {
                case null:
                    continue;
                case IInvocationOperation:
                    return "call";
                case IObjectCreationOperation:
                    return "new";
                case IMethodReferenceOperation:
                case IDelegateCreationOperation { Target: IMethodReferenceOperation }:
                    return "method group";
                case IMemberReferenceOperation member when member.Parent is IAssignmentOperation assignment && assignment.Target == member:
                    return "write";
                case IMemberReferenceOperation:
                    return "read";
                default:
                    return "reference";
            }
        }

        return "reference";
    }
}

/// <summary>Merges hits by position, accumulating the target frameworks each one was found in.</summary>
internal sealed class SourceHitCollector(WorkspaceSnapshot snapshot)
{
    private readonly Dictionary<(string Path, int Line, int Column), (SourceHit Hit, SortedSet<string> Frameworks)> _hits = [];

    public async Task AddAsync(Document document, Location location, string kind, string? via, CancellationToken cancellationToken)
    {
        if (document.FilePath is null)
        {
            return;
        }

        var span = location.GetLineSpan();
        var key = (document.FilePath, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
        var project = snapshot.Projects.Get(document.Project.Id);
        if (!_hits.TryGetValue(key, out var entry))
        {
            var text = await document.GetTextAsync(cancellationToken);
            var snippet = text.Lines[span.StartLinePosition.Line].ToString().Trim();
            if (snippet.Length > ReferenceFinder.MaxSnippetLength)
            {
                snippet = string.Concat(snippet.AsSpan(0, ReferenceFinder.MaxSnippetLength), "…");
            }

            entry = (new SourceHit(key.Item1, key.Item2, key.Item3, snippet, project?.Name ?? document.Project.Name, [], kind, via), new SortedSet<string>(StringComparer.OrdinalIgnoreCase));
            _hits[key] = entry;
        }

        if (project?.TargetFramework is { } framework)
        {
            entry.Frameworks.Add(framework);
        }
    }

    /// <summary>Markup is not compiled per target framework, so its hits list none.</summary>
    public void AddMarkup(MarkupReference reference)
    {
        var key = (reference.FilePath, reference.Line, reference.Column);
        _hits.TryAdd(key, (new SourceHit(reference.FilePath, reference.Line, reference.Column, reference.Snippet, reference.Project, [], "markup"), new SortedSet<string>(StringComparer.OrdinalIgnoreCase)));
    }

    public List<SourceHit> ToList() =>
        [.. _hits.Values
            .Select(e => e.Hit with { TargetFrameworks = [.. e.Frameworks] })
            .OrderBy(h => h.Project, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.Line)
            .ThenBy(h => h.Column)];
}
