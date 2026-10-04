using Farol.Engine.Markup;
using Farol.Engine.Workspaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;

namespace Farol.Engine.Navigation;

/// <summary>One place in source. Found in several target frameworks, it is still one hit that lists them.</summary>
public sealed record SourceHit(string FilePath, int Line, int Column, string Snippet, string Project, IReadOnlyList<string> TargetFrameworks, string Kind);

public sealed record ReferenceResult(IReadOnlyList<SourceHit> Definitions, IReadOnlyList<SourceHit> References);

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
        var locations = new List<ReferenceLocation>();
        foreach (var variant in candidate.Variants)
        {
            var found = await SymbolFinder.FindReferencesAsync(variant.Symbol, snapshot.Solution, cancellationToken);

            // Only the symbol itself: cascaded results (implementations, overrides) belong to dotnet_hierarchy.
            foreach (var referenced in found.Where(r => SymbolFormatter.Id(r.Definition) == candidate.Id))
            {
                foreach (var location in referenced.Definition.Locations.Where(l => l.IsInSource))
                {
                    if (snapshot.Solution.GetDocument(location.SourceTree) is { } document)
                    {
                        await definitions.AddAsync(document, location, "definition", cancellationToken);
                    }
                }

                locations.AddRange(referenced.Locations.Where(r => !r.IsImplicit && r.Location.IsInSource));
            }
        }

        // Classifying binds the code around each use; for a type used a thousand times, one after the other, that was
        // most of the call. Semantic models are safe to share across threads.
        var kinds = new string[locations.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, locations.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken },
            async (index, token) => kinds[index] = await ClassifyAsync(locations[index], token));
        for (var index = 0; index < locations.Count; index++)
        {
            await references.AddAsync(locations[index].Document, locations[index].Location, kinds[index], cancellationToken);
        }

        var markup = await MarkupIndex.GetAsync(snapshot, cancellationToken);
        foreach (var reference in markup.ReferencesTo(candidate.Id))
        {
            references.AddMarkup(reference);
        }

        return new ReferenceResult(definitions.ToList(), references.ToList());
    }

    private static async Task<string> ClassifyAsync(ReferenceLocation reference, CancellationToken cancellationToken)
    {
        var root = await reference.Document.GetSyntaxRootAsync(cancellationToken);
        var model = await reference.Document.GetSemanticModelAsync(cancellationToken);
        var node = root!.FindNode(reference.Location.SourceSpan, getInnermostNodeForTie: true);
        var depth = 0;
        for (var current = node; current is not null && depth < 4; current = current.Parent, depth++)
        {
            switch (model!.GetOperation(current, cancellationToken))
            {
                case null:
                    continue;
                case IInvocationOperation:
                    return "call";
                case IObjectCreationOperation:
                    return "new";
                case IMethodReferenceOperation:
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

    public async Task AddAsync(Document document, Location location, string kind, CancellationToken cancellationToken)
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

            entry = (new SourceHit(key.Item1, key.Item2, key.Item3, snippet, project?.Name ?? document.Project.Name, [], kind), new SortedSet<string>(StringComparer.OrdinalIgnoreCase));
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
