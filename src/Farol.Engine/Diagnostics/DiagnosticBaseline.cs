using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

namespace Farol.Engine.Diagnostics;

/// <summary>
/// The diagnostics of the load snapshot, computed per project or per file the first time a check needs them and
/// kept for as long as that load lives: a reload brings a new load solution, hence a fresh baseline.
/// </summary>
internal sealed class DiagnosticBaseline
{
    private static readonly ConditionalWeakTable<Solution, DiagnosticBaseline> Instances = new();

    private readonly Solution _loaded;
    private readonly ConcurrentDictionary<ProjectId, Lazy<Task<DiagnosticSet>>> _projects = new();
    private readonly ConcurrentDictionary<DocumentId, Lazy<Task<DiagnosticSet>>> _documents = new();

    private DiagnosticBaseline(Solution loaded)
    {
        _loaded = loaded;
    }

    public static DiagnosticBaseline For(Solution loaded) => Instances.GetValue(loaded, s => new DiagnosticBaseline(s));

    // Computed without the caller's token: the result is shared and stays valid, so a cancelled request must not poison it.
    // On the thread pool, so callers can compute the current diagnostics while the baseline's are computed.
    public Task<DiagnosticSet> ProjectAsync(ProjectId id, CancellationToken cancellationToken) =>
        _projects.GetOrAdd(id, key => new Lazy<Task<DiagnosticSet>>(() => Task.Run(() => ComputeAsync(_loaded.GetProject(key))))).Value.WaitAsync(cancellationToken);

    public Task<DiagnosticSet> DocumentAsync(DocumentId id, CancellationToken cancellationToken) =>
        _documents.GetOrAdd(id, key => new Lazy<Task<DiagnosticSet>>(() => Task.Run(() => ComputeAsync(_loaded.GetDocument(key))))).Value.WaitAsync(cancellationToken);

    private static async Task<DiagnosticSet> ComputeAsync(Project? project) =>
        project is null ? DiagnosticSet.Empty : DiagnosticSet.From(await CompilerDiagnostics.ForProjectAsync(project, CancellationToken.None));

    private static async Task<DiagnosticSet> ComputeAsync(Document? document) =>
        document is null ? DiagnosticSet.Empty : DiagnosticSet.From(await CompilerDiagnostics.ForDocumentAsync(document, CancellationToken.None));
}

/// <summary>Errors and warnings as the build reports them (severity overrides, #pragma and NoWarn applied).</summary>
internal static class CompilerDiagnostics
{
    public static async Task<ImmutableArray<Diagnostic>> ForProjectAsync(Project project, CancellationToken cancellationToken)
    {
        var compilation = await project.GetCompilationAsync(cancellationToken);
        return compilation is null ? [] : Reportable(compilation.GetDiagnostics(cancellationToken));
    }

    /// <summary>Syntax, declaration and member-body diagnostics located in one file.</summary>
    public static async Task<ImmutableArray<Diagnostic>> ForDocumentAsync(Document document, CancellationToken cancellationToken)
    {
        var model = await document.GetSemanticModelAsync(cancellationToken);
        return model is null ? [] : Reportable(model.GetDiagnostics(cancellationToken: cancellationToken));
    }

    public static ImmutableArray<Diagnostic> ForCompilation(Compilation? compilation, CancellationToken cancellationToken) =>
        compilation is null ? [] : Reportable(compilation.GetDiagnostics(cancellationToken));

    /// <summary>The diagnostics located in one file, bound against a given compilation of its project.</summary>
    public static ImmutableArray<Diagnostic> ForTree(Compilation? compilation, SyntaxTree? tree, CancellationToken cancellationToken) =>
        compilation is null || tree is null ? [] : Reportable(compilation.GetSemanticModel(tree).GetDiagnostics(cancellationToken: cancellationToken));

    private static ImmutableArray<Diagnostic> Reportable(ImmutableArray<Diagnostic> diagnostics) =>
        [.. diagnostics.Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning && !d.IsSuppressed)];
}

/// <summary>
/// Identifies a diagnostic across edits without its line number: id, file, message and the trimmed text of its
/// line. Inserting lines above a warning moves it without making it new; editing its own line does.
/// </summary>
internal readonly record struct DiagnosticFingerprint(string Id, string? FilePath, string Message, string LineText)
{
    public static DiagnosticFingerprint Of(Diagnostic diagnostic)
    {
        var message = diagnostic.GetMessage(CultureInfo.InvariantCulture);
        if (diagnostic.Location.SourceTree is not { } tree)
        {
            return new DiagnosticFingerprint(diagnostic.Id, null, message, string.Empty);
        }

        var text = tree.GetText();
        var line = text.Lines.GetLineFromPosition(Math.Min(diagnostic.Location.SourceSpan.Start, text.Length));
        return new DiagnosticFingerprint(diagnostic.Id, tree.FilePath, message, line.ToString().Trim());
    }
}

/// <summary>A multiset of fingerprints: two identical warnings at load allow two identical warnings now.</summary>
internal sealed class DiagnosticSet
{
    public static readonly DiagnosticSet Empty = new([]);

    private readonly Dictionary<DiagnosticFingerprint, int> _counts;

    private DiagnosticSet(Dictionary<DiagnosticFingerprint, int> counts)
    {
        _counts = counts;
    }

    public static DiagnosticSet From(IEnumerable<Diagnostic> diagnostics)
    {
        var counts = new Dictionary<DiagnosticFingerprint, int>();
        foreach (var diagnostic in diagnostics)
        {
            var fingerprint = DiagnosticFingerprint.Of(diagnostic);
            counts[fingerprint] = counts.GetValueOrDefault(fingerprint) + 1;
        }

        return new DiagnosticSet(counts);
    }

    /// <summary>Splits <paramref name="current"/> into diagnostics this set does not account for and those it does.</summary>
    public (List<Diagnostic> New, List<Diagnostic> Existing) Split(IEnumerable<Diagnostic> current)
    {
        var remaining = new Dictionary<DiagnosticFingerprint, int>(_counts);
        var fresh = new List<Diagnostic>();
        var existing = new List<Diagnostic>();
        foreach (var diagnostic in current)
        {
            var fingerprint = DiagnosticFingerprint.Of(diagnostic);
            if (remaining.TryGetValue(fingerprint, out var count) && count > 0)
            {
                remaining[fingerprint] = count - 1;
                existing.Add(diagnostic);
            }
            else
            {
                fresh.Add(diagnostic);
            }
        }

        return (fresh, existing);
    }
}
