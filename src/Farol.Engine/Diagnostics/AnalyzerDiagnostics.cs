using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Farol.Engine.Diagnostics;

/// <summary>
/// What running the project analyzers took: how long, on how many files, with how many analyzers, which threw
/// (and the exception), and which analyzer assemblies did not finish within the time limit.
/// </summary>
public sealed record AnalyzerRun(TimeSpan Elapsed, int Files, int Analyzers, IReadOnlyList<string> Failures, IReadOnlyList<string> Unfinished);

/// <summary>
/// A file's analyzer diagnostics at load, the problems of that run (failures and assemblies that did not finish kept
/// apart), and the rule ids it could not cover: findings of those rules are not called new, since the load said nothing.
/// </summary>
internal sealed record AnalyzerBaseline(DiagnosticSet Diagnostics, IReadOnlyList<string> Failures, IReadOnlyList<string> Unfinished, IReadOnlySet<string> Uncovered)
{
    public bool Complete => Failures.Count == 0 && Unfinished.Count == 0;
}

/// <summary>One analyzer pass over some files of a project; <see cref="Uncovered"/> holds the rule ids of the analyzers that failed or did not finish.</summary>
internal sealed record AnalyzerPass(ImmutableArray<Diagnostic> Diagnostics, int Analyzers, IReadOnlyList<string> Failures, IReadOnlyList<string> Unfinished, IReadOnlySet<string> Uncovered);

/// <summary>
/// The diagnostics of the analyzers a project references (NuGet analyzers, the SDK's CA rules, code style when the build
/// enforces it), as the build would report them: severities from the compilation options and .editorconfig, warnings
/// as errors applied, suppressed ones left out. Each analyzer assembly runs on its own, under a shared time limit, with
/// every assembly's suppressors beside it so a suppressor from one package still silences another package's rule.
/// </summary>
internal static class AnalyzerDiagnostics
{
    // AnalyzerFileReference reports a load failure through an event, once per reference: kept so every pass names it.
    // One Lazy per reference and language, so a single thread subscribes and loads even when checks run side by side.
    private static readonly ConditionalWeakTable<AnalyzerReference, ConcurrentDictionary<string, Lazy<LoadedAnalyzers>>> Loaded = [];

    private sealed record LoadedAnalyzers(ImmutableArray<DiagnosticAnalyzer> Analyzers, IReadOnlyList<string> Problems);

    // One analysis per compilation with every analyzer of its project: WithAnalyzers clones the compilation, whose symbols
    // are then built again, which on a large project took seconds per analyzer assembly and per check. The load's
    // compilation never changes, and an edited one is reused by every check until the next edit.
    private static readonly ConditionalWeakTable<Compilation, Lazy<SharedAnalysis>> Analyses = [];

    /// <summary>A compilation's analysis, and the analyzers that threw in it (reported by every pass that runs them).</summary>
    private sealed class SharedAnalysis
    {
        public SharedAnalysis(Project project, Compilation compilation, ImmutableArray<DiagnosticAnalyzer> analyzers)
        {
            var options = new CompilationWithAnalyzersOptions(
                project.AnalyzerOptions,
                onAnalyzerException: (exception, analyzer, _) => Exceptions.TryAdd(analyzer, $"{exception.GetType().Name}: {exception.Message}"),
                concurrentAnalysis: true,
                logAnalyzerExecutionTime: false,
                reportSuppressedDiagnostics: false);
            Analysis = compilation.WithAnalyzers(analyzers, options);
        }

        public CompilationWithAnalyzers Analysis { get; }

        public ConcurrentDictionary<DiagnosticAnalyzer, string> Exceptions { get; } = new();
    }

    /// <summary>The analyzer diagnostics located in the given files of one project's compilation.</summary>
    public static async Task<AnalyzerPass> ForTreesAsync(
        Project project, Compilation compilation, IReadOnlyList<SyntaxTree> trees, TimeSpan limit, CancellationToken cancellationToken)
    {
        var failures = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var uncovered = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var groups = new List<(string Name, ImmutableArray<DiagnosticAnalyzer> Analyzers)>();
        foreach (var reference in project.AnalyzerReferences)
        {
            var loaded = Load(reference, project.Language);
            if (loaded.Problems.Count > 0)
            {
                failures.TryAdd(Name(reference), string.Join("; ", loaded.Problems.Distinct(StringComparer.Ordinal)));
            }

            if (!loaded.Analyzers.IsEmpty)
            {
                groups.Add((Name(reference), loaded.Analyzers));
            }
        }

        if (groups.Count == 0 || trees.Count == 0)
        {
            return new([], 0, Sorted(failures), [], new HashSet<string>(StringComparer.Ordinal));
        }

        var suppressors = groups.SelectMany(g => g.Analyzers.OfType<DiagnosticSuppressor>()).ToImmutableArray();
        var shared = Analyses.GetValue(compilation, c => new Lazy<SharedAnalysis>(() => new SharedAnalysis(project, c, [.. groups.SelectMany(g => g.Analyzers).Distinct()]))).Value;
        var unfinished = new ConcurrentBag<string>();
        var found = new ConcurrentBag<Diagnostic>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = timeout.Token; // read once: work abandoned past the limit must not touch the source after it is disposed
        timeout.CancelAfter(limit);
        try
        {
            await Task.WhenAll(groups.Select(async group =>
            {
                void Uncover(IEnumerable<DiagnosticAnalyzer> analyzers)
                {
                    foreach (var id in analyzers.SelectMany(Rules))
                    {
                        uncovered.TryAdd(id, 0);
                    }
                }

                var work = Task.Run(
                async () =>
                {
                    var local = new List<Diagnostic>();
                    try
                    {
                        // The group's analyzers, with every assembly's suppressors, in the compilation's one analysis.
                        var analyzers = group.Analyzers.AddRange(suppressors.Where(s => !group.Analyzers.Contains(s)));

                        // Passes share the analysis, and one may await work another started: a cancellation that is not
                        // this pass's (the other's limit, or its caller) is retried once instead of failing the group.
                        async Task<ImmutableArray<Diagnostic>> RunAsync(Func<Task<ImmutableArray<Diagnostic>>> call)
                        {
                            try
                            {
                                return await call();
                            }
                            catch (OperationCanceledException) when (!token.IsCancellationRequested)
                            {
                                return await call();
                            }
                        }

                        // The files side by side: a changed declaration binds files across the project, each analyzed alone.
                        var perTree = new ConcurrentBag<ImmutableArray<Diagnostic>>();
                        await Parallel.ForEachAsync(trees, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Environment.ProcessorCount }, async (tree, _) =>
                        {
                            perTree.Add(await RunAsync(() => shared.Analysis.GetAnalyzerSyntaxDiagnosticsAsync(tree, analyzers, token)));
                            perTree.Add(await RunAsync(() => shared.Analysis.GetAnalyzerSemanticDiagnosticsAsync(shared.Analysis.Compilation.GetSemanticModel(tree), filterSpan: null, analyzers, token)));
                        });
                        foreach (var diagnostics in perTree)
                        {
                            local.AddRange(diagnostics);
                        }

                        foreach (var analyzer in group.Analyzers)
                        {
                            if (shared.Exceptions.TryGetValue(analyzer, out var message))
                            {
                                failures.TryAdd($"{analyzer.GetType().Name} ({group.Name})", message);
                                Uncover([analyzer]);
                            }
                        }

                        return local;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    {
                        return null;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        failures.TryAdd(group.Name, $"{ex.GetType().Name}: {ex.Message}");
                        Uncover(group.Analyzers);
                        return local;
                    }
                },
                CancellationToken.None);

                // An analyzer that ignores cancellation keeps its thread: the check does not wait for it past the limit.
                List<Diagnostic>? diagnostics;
                try
                {
                    diagnostics = await work.WaitAsync(limit + TimeSpan.FromSeconds(1), cancellationToken);
                }
                catch (TimeoutException)
                {
                    diagnostics = null;
                }

                if (diagnostics is null)
                {
                    unfinished.Add(group.Name);
                    Uncover(group.Analyzers);
                }
                else
                {
                    found.AddRange(diagnostics);
                }
            }));

            // An analyzer still running past the limit would hold its trees in the shared analysis, and the next pass
            // on this compilation would wait for it: that pass starts a fresh analysis instead.
            if (!unfinished.IsEmpty)
            {
                Analyses.Remove(compilation);
            }
        }
        finally
        {
            timeout.Cancel();
        }

        cancellationToken.ThrowIfCancellationRequested();
        var paths = trees.Select(t => t.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reportable = found
            .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning && !d.IsSuppressed)
            .Where(d => d.Location.SourceTree is { } tree && paths.Contains(tree.FilePath))
            .Distinct()
            .ToImmutableArray();
        return new(
            reportable,
            groups.Sum(g => g.Analyzers.Length),
            Sorted(failures),
            [.. unfinished.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            uncovered.Keys.ToHashSet(StringComparer.Ordinal));
    }

    private static List<string> Sorted(ConcurrentDictionary<string, string> failures) =>
        [.. failures.Select(f => $"{f.Key}: {f.Value}").Order(StringComparer.Ordinal)];

    private static string[] Rules(DiagnosticAnalyzer analyzer)
    {
        try
        {
            return [.. analyzer.SupportedDiagnostics.Select(d => d.Id)];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
    }

    // Unresolved references and assemblies that do not load come back empty instead of throwing: they are named here.
    private static LoadedAnalyzers Load(AnalyzerReference reference, string language)
    {
        if (reference is UnresolvedAnalyzerReference)
        {
            return new([], [$"not found: {reference.Display ?? reference.FullPath}"]);
        }

        var byLanguage = Loaded.GetValue(reference, _ => new ConcurrentDictionary<string, Lazy<LoadedAnalyzers>>(StringComparer.Ordinal));
        return byLanguage.GetOrAdd(language, l => new Lazy<LoadedAnalyzers>(() => LoadNow(reference, l))).Value;
    }

    private static LoadedAnalyzers LoadNow(AnalyzerReference r, string language)
    {
        var problems = new List<string>();
        void OnFailed(object? sender, AnalyzerLoadFailureEventArgs e) =>
            problems.Add($"could not be loaded: {e.ErrorCode}{(e.TypeName is null ? string.Empty : $" ({e.TypeName})")}: {e.Message}");

        var file = r as AnalyzerFileReference;
        if (file is not null)
        {
            file.AnalyzerLoadFailed += OnFailed;
        }

        try
        {
            return new(r.GetAnalyzers(language), problems);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            problems.Add($"could not be loaded: {ex.GetType().Name}: {ex.Message}");
            return new([], problems);
        }
        finally
        {
            if (file is not null)
            {
                file.AnalyzerLoadFailed -= OnFailed;
            }
        }
    }

    private static string Name(AnalyzerReference reference) =>
        reference.FullPath is { Length: > 0 } path ? Path.GetFileNameWithoutExtension(path) : reference.Display ?? "analyzer";

    /// <summary>Times a run and gathers what the projects report into one <see cref="AnalyzerRun"/>.</summary>
    public sealed class Tally
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly ConcurrentBag<string> _failures = [];
        private readonly ConcurrentBag<string> _unfinished = [];
        private readonly ConcurrentDictionary<string, byte> _files = new(StringComparer.OrdinalIgnoreCase);
        private int _analyzers;

        public void Add(IEnumerable<string> files, int analyzers, IEnumerable<string> failures, IEnumerable<string> unfinished)
        {
            foreach (var file in files)
            {
                _files.TryAdd(file, 0);
            }

            InterlockedMax(ref _analyzers, analyzers);
            _failures.AddRange(failures);
            _unfinished.AddRange(unfinished);
        }

        public AnalyzerRun Finish() => new(
            _clock.Elapsed,
            _files.Count,
            _analyzers,
            [.. _failures.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            [.. _unfinished.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }
    }
}

internal static class ConcurrentBagExtensions
{
    public static void AddRange<T>(this ConcurrentBag<T> bag, IEnumerable<T> items)
    {
        foreach (var item in items)
        {
            bag.Add(item);
        }
    }
}
