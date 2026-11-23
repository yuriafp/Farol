using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using Farol.Core;
using Farol.Engine.Workspaces;
using Microsoft.CodeAnalysis;

namespace Farol.Engine.Diagnostics;

/// <summary>
/// dotnet_check: compiles what an edit can affect and reports the diagnostics the load baseline does not have.
/// Edits inside member bodies re-check only the edited files. Declaration changes re-check the edited files plus the
/// files, in any project and either language, that use what changed (<see cref="EditImpact"/>), so a C# signature
/// change surfaces as the VB error it causes; changes a name cannot bound re-check the dependent projects whole.
/// </summary>
public static class DiagnosticCheck
{
    private const int MinFilesForProjectCheck = 64;

    public static async Task<CheckResult> RunAsync(WorkspaceSnapshot snapshot, CheckRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(request);

        var rewrites = new BaselineRewrites([.. snapshot.Load.Renames], [.. snapshot.Load.Moves]) { LineEdits = [.. snapshot.Load.LineEdits] };
        var plan = request.Scope switch
        {
            CheckScope.Changed => await PlanChangedAsync(snapshot, request.IncludeExisting, cancellationToken),
            CheckScope.File => PlanFile(snapshot, request.FilePath),
            CheckScope.Project => PlanProject(snapshot, request.Project),
            _ => new CheckPlan([.. snapshot.Solution.ProjectIds], [], []),
        };

        var solution = snapshot.Solution;
        var loaded = snapshot.Load.Solution;
        var unseen = snapshot.Load.ChangedDuringLoad;
        var baseline = DiagnosticBaseline.For(loaded);
        var compilations = CheckCompilations.For(loaded, solution);
        var found = new ConcurrentBag<(ProjectId Project, List<Diagnostic> New, List<Diagnostic> Existing)>();
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
            CancellationToken = cancellationToken,
        };

        // The current diagnostics and the baseline's (computed once per load) are independent: compute them side by side.
        await Parallel.ForEachAsync(plan.Projects, options, async (id, token) =>
        {
            var atLoad = baseline.ProjectAsync(id, token);
            var current = CompilerDiagnostics.ForCompilation(await compilations.GetAsync(id, token), token);
            var (fresh, existing) = WithUnseenAsNew((await atLoad).Split(current, rewrites), unseen);
            found.Add((id, fresh, existing));
        });

        // A file's baseline binds it all again as it was at load. With fewer files than processors, it is computed beside
        // the current diagnostics, on a processor that would idle otherwise. With more, the processors are busy, and it
        // is only computed for files that have diagnostics now: a file with none has nothing new.
        var beside = plan.Documents.Count < Environment.ProcessorCount;
        var perFile = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(plan.Documents, perFile, async (id, token) =>
        {
            var atLoad = beside ? baseline.DocumentAsync(id, token) : null;
            var current = compilations.IsEdited(id.ProjectId)
                ? await CompilerDiagnostics.ForDocumentAsync(solution.GetDocument(id)!, token)
                : CompilerDiagnostics.ForTree(await compilations.GetAsync(id.ProjectId, token), await loaded.GetDocument(id)!.GetSyntaxTreeAsync(token), token);
            var (fresh, existing) = current.IsEmpty ? ([], []) : WithUnseenAsNew((await (atLoad ?? baseline.DocumentAsync(id, token))).Split(current, rewrites), unseen);
            found.Add((id.ProjectId, fresh, existing));
        });

        AnalyzerRun? analyzerRun = null;
        if (request.Analyzers)
        {
            var limit = request.AnalyzerLimit ?? DefaultAnalyzerLimit;
            var tally = new AnalyzerDiagnostics.Tally();
            var targets = AnalyzerTargets(snapshot, plan, request.Scope);
            await Parallel.ForEachAsync(targets.GroupBy(id => id.ProjectId), options, async (group, token) =>
            {
                if (solution.GetProject(group.Key) is not { } project || await compilations.GetAsync(group.Key, token) is not { } compilation)
                {
                    return;
                }

                // The trees of the compilation the files are bound against: the current one for an edited project, else the load's.
                var source = compilations.IsEdited(group.Key) ? solution : loaded;
                var documents = new List<(DocumentId Id, SyntaxTree Tree)>();
                foreach (var id in group)
                {
                    if (source.GetDocument(id) is { } document && await document.GetSyntaxTreeAsync(token) is { } tree)
                    {
                        documents.Add((id, tree));
                    }
                }

                // The load's analyzer diagnostics of these files (a moved file's under its load path) are computed beside
                // the current ones, under the same limit.
                var atLoadIds = documents.ToDictionary(d => d.Id, d => AtLoad(snapshot, loaded, group.Key, d.Id, d.Tree.FilePath));
                var atLoad = baseline.AnalyzerDocumentsAsync(group.Key, [.. atLoadIds.Values.OfType<DocumentId>()], limit, token);
                var pass = AnalyzerDiagnostics.ForTreesAsync(project, compilation, [.. documents.Select(d => d.Tree)], limit, token);
                await Task.WhenAll(atLoad, pass);
                var (before, run) = (atLoad.Result, pass.Result);
                tally.Add(
                    documents.Select(d => d.Tree.FilePath),
                    run.Analyzers,
                    run.Failures.Concat(before.Values.SelectMany(b => b.Failures).Select(p => $"at load: {p}")),
                    run.Unfinished.Concat(before.Values.SelectMany(b => b.Unfinished).Select(p => $"at load: {p}")));
                foreach (var (id, tree) in documents)
                {
                    var current = run.Diagnostics.Where(d => d.Location.SourceTree == tree).ToImmutableArray();
                    if (current.IsEmpty)
                    {
                        continue;
                    }

                    var known = atLoadIds[id] is { } loadId && before.TryGetValue(loadId, out var b) ? b : null;
                    var (fresh, existing) = WithUnseenAsNew((known?.Diagnostics ?? DiagnosticSet.Empty).Split(current, rewrites), unseen);

                    // A rule the load could not run (it threw or ran out of time there) has no baseline: its findings are not called new.
                    if (known is { Uncovered.Count: > 0 } && !unseen.Contains(tree.FilePath, StringComparer.OrdinalIgnoreCase))
                    {
                        existing = [.. existing, .. fresh.Where(d => known.Uncovered.Contains(d.Id))];
                        fresh = [.. fresh.Where(d => !known.Uncovered.Contains(d.Id))];
                    }

                    found.Add((group.Key, fresh, existing));
                }
            });

            analyzerRun = tally.Finish();
        }

        return new CheckResult(
            Merge(snapshot, found.SelectMany(f => f.New.Select(d => (f.Project, d)))),
            request.IncludeExisting ? Merge(snapshot, found.SelectMany(f => f.Existing.Select(d => (f.Project, d)))) : [],
            plan.ChangedFiles,
            [.. plan.Projects.Select(id => ProjectName(snapshot, id)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)],
            [.. plan.Documents.Select(id => solution.GetDocument(id)?.FilePath).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)],
            [.. unseen.Where(path => solution.GetDocumentIdsWithFilePath(path).Any(id => plan.Projects.Contains(id.ProjectId) || plan.Documents.Contains(id)))
                .Order(StringComparer.OrdinalIgnoreCase)])
        {
            Analyzers = analyzerRun,
        };
    }

    /// <summary>The time analyzers get on one check, unless Farol:AnalyzerTimeoutSeconds says otherwise.</summary>
    public static readonly TimeSpan DefaultAnalyzerLimit = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The files analyzers run on: the one file, the project's files, or the files the check binds plus the edited
    /// ones (the whole solution would take minutes; its edited files are what a change can break).
    /// </summary>
    private static List<DocumentId> AnalyzerTargets(WorkspaceSnapshot snapshot, CheckPlan plan, CheckScope scope)
    {
        var solution = snapshot.Solution;
        IEnumerable<DocumentId> targets = scope switch
        {
            CheckScope.File => plan.Documents,
            CheckScope.Project => plan.Projects.SelectMany(id => solution.GetProject(id)?.DocumentIds ?? []),
            CheckScope.Changed => plan.Bound.Concat(Edited(snapshot)),
            _ => Edited(snapshot),
        };
        return [.. targets.Distinct()];
    }

    /// <summary>The file's document in the loaded solution: the same id, or for a file Farol moved, the document at its load path.</summary>
    private static DocumentId? AtLoad(WorkspaceSnapshot snapshot, Solution loaded, ProjectId project, DocumentId id, string? path)
    {
        if (loaded.GetDocument(id) is not null)
        {
            return id;
        }

        var moves = snapshot.Load.Moves.ToArray();
        var moved = false;
        for (var i = moves.Length - 1; path is not null && i >= 0; i--)
        {
            if (string.Equals(moves[i].To, path, StringComparison.OrdinalIgnoreCase))
            {
                path = moves[i].From;
                moved = true;
            }
        }

        return moved && path is not null ? loaded.GetDocumentIdsWithFilePath(path).FirstOrDefault(d => d.ProjectId == project) : null;
    }

    private static IEnumerable<DocumentId> Edited(WorkspaceSnapshot snapshot)
    {
        var current = snapshot.Solution;
        var changes = current.GetChanges(snapshot.Load.Solution).GetProjectChanges();
        return changes.SelectMany(p => p.GetChangedDocuments(onlyGetDocumentsWithTextChanges: true).Concat(p.GetAddedDocuments()))
            .Concat(snapshot.Load.ChangedDuringLoad.SelectMany(path => current.GetDocumentIdsWithFilePath(path)));
    }

    /// <summary>
    /// A file that changed while the workspace loaded may have been read already changed: its load diagnostics are no
    /// baseline, so all of its diagnostics are new.
    /// </summary>
    private static (List<Diagnostic> New, List<Diagnostic> Existing) WithUnseenAsNew((List<Diagnostic> New, List<Diagnostic> Existing) split, IReadOnlySet<string> unseen)
    {
        if (unseen.Count == 0)
        {
            return split;
        }

        var moved = split.Existing.Where(d => d.Location.SourceTree?.FilePath is { } path && unseen.Contains(path)).ToList();
        return moved.Count == 0 ? split : ([.. split.New, .. moved], [.. split.Existing.Except(moved)]);
    }

    private static async Task<CheckPlan> PlanChangedAsync(WorkspaceSnapshot snapshot, bool includeExisting, CancellationToken cancellationToken)
    {
        var loaded = snapshot.Load.Solution;
        var current = snapshot.Solution;
        var changedFiles = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var documents = new HashSet<DocumentId>();
        var wholeProjects = new HashSet<ProjectId>();
        var withDependents = new HashSet<ProjectId>();

        // Changed while the workspace loaded, a file can equal its load text: it is checked as an edited file all the same.
        foreach (var path in snapshot.Load.ChangedDuringLoad)
        {
            var ids = current.GetDocumentIdsWithFilePath(path);
            if (ids.Length > 0)
            {
                changedFiles.Add(path);
                documents.UnionWith(ids);
            }
        }

        foreach (var project in current.GetChanges(loaded).GetProjectChanges())
        {
            var added = project.GetAddedDocuments().ToList();
            var removed = project.GetRemovedDocuments().ToList();
            var changed = project.GetChangedDocuments(onlyGetDocumentsWithTextChanges: true).ToList();
            AddPaths(changedFiles, added.Select(id => current.GetDocument(id)?.FilePath));
            AddPaths(changedFiles, removed.Select(id => loaded.GetDocument(id)?.FilePath));
            AddPaths(changedFiles, changed.Select(id => current.GetDocument(id)?.FilePath));

            var declarationEdits = new List<DocumentId>(added.Concat(removed));
            foreach (var id in changed)
            {
                if (await SameDeclarationsAsync(loaded.GetDocument(id)!, current.GetDocument(id)!, cancellationToken))
                {
                    documents.Add(id);
                }
                else
                {
                    declarationEdits.Add(id);
                }
            }

            foreach (var id in declarationEdits)
            {
                var impact = await EditImpact.OfAsync(loaded, current, id, cancellationToken);
                switch (impact.Reach)
                {
                    case ImpactReach.Files:
                        documents.UnionWith(impact.Documents);
                        break;
                    case ImpactReach.Project:
                        wholeProjects.Add(project.ProjectId);
                        break;
                    default:
                        withDependents.Add(project.ProjectId);
                        break;
                }
            }
        }

        var graph = current.GetProjectDependencyGraph();
        var projects = new HashSet<ProjectId>(wholeProjects);
        foreach (var id in withDependents)
        {
            projects.Add(id);
            projects.UnionWith(graph.GetProjectsThatTransitivelyDependOnThisProject(id));
        }

        // Existing diagnostics are listed per project, so every project with a file to check is checked whole too.
        if (includeExisting)
        {
            projects.UnionWith(documents.Select(d => d.ProjectId));
        }

        // Past a share of a project's files, one compilation of the project is cheaper than file by file.
        foreach (var group in documents.GroupBy(d => d.ProjectId))
        {
            if (current.GetProject(group.Key) is { } project && group.Count() > Math.Max(MinFilesForProjectCheck, project.DocumentIds.Count / 2))
            {
                projects.Add(group.Key);
            }
        }

        return new CheckPlan([.. projects], [.. documents.Where(d => !projects.Contains(d.ProjectId))], [.. changedFiles]) { Bound = [.. documents] };
    }

    // Edits inside bodies cannot change what other files or projects bind to; const initializers count as declarations.
    private static async Task<bool> SameDeclarationsAsync(Document before, Document after, CancellationToken cancellationToken)
    {
        var old = await before.GetSyntaxTreeAsync(cancellationToken);
        var updated = await after.GetSyntaxTreeAsync(cancellationToken);
        return old is not null && updated is not null && old.IsEquivalentTo(updated, topLevel: true);
    }

    private static CheckPlan PlanFile(WorkspaceSnapshot snapshot, string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new FarolException(ErrorCodes.InvalidArgument, "scope='file' needs 'path'.", "Pass the source file, relative to the root as responses write paths.");
        }

        return new CheckPlan([], [.. SourceDocuments.Find(snapshot, filePath, Path.GetFileName(filePath))], []);
    }

    private static CheckPlan PlanProject(WorkspaceSnapshot snapshot, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new FarolException(ErrorCodes.InvalidArgument, "scope='project' needs 'project'.", "Pass a project name as dotnet_overview lists it.");
        }

        var variants = snapshot.Projects.FindByName(name);
        if (variants.Count == 0)
        {
            throw new FarolException(
                ErrorCodes.InvalidArgument,
                $"No project named '{name}'.",
                $"Projects: {string.Join(", ", snapshot.Projects.Names.Take(40))}.");
        }

        return new CheckPlan([.. variants.Select(v => v.Id)], [], []);
    }

    private static List<DiagnosticEntry> Merge(WorkspaceSnapshot snapshot, IEnumerable<(ProjectId Project, Diagnostic Diagnostic)> found)
    {
        var merged = new Dictionary<(string Id, string? Path, int Line, int Column, string Message, string Project), (DiagnosticEntry Entry, SortedSet<string> Frameworks)>();
        foreach (var (projectId, diagnostic) in found)
        {
            var span = diagnostic.Location.GetLineSpan();
            var path = diagnostic.Location.IsInSource ? span.Path : null;
            var line = path is null ? 0 : span.StartLinePosition.Line + 1;
            var column = path is null ? 0 : span.StartLinePosition.Character + 1;
            var message = diagnostic.GetMessage(CultureInfo.InvariantCulture);
            var project = ProjectName(snapshot, projectId);
            var key = (diagnostic.Id, path, line, column, message, project);
            if (!merged.TryGetValue(key, out var entry))
            {
                entry = (new DiagnosticEntry(diagnostic.Id, diagnostic.Severity, message, path, line, column, project, []), new SortedSet<string>(StringComparer.OrdinalIgnoreCase));
                merged[key] = entry;
            }

            if (snapshot.Projects.IsMultiTargeted(projectId) && snapshot.Projects.Get(projectId)?.TargetFramework is { } framework)
            {
                entry.Frameworks.Add(framework);
            }
        }

        return [.. merged.Values
            .Select(e => e.Entry with { TargetFrameworks = [.. e.Frameworks] })
            .OrderBy(e => e.Severity == DiagnosticSeverity.Error ? 0 : 1)
            .ThenBy(e => e.Project, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Line)
            .ThenBy(e => e.Column)];
    }

    private static string ProjectName(WorkspaceSnapshot snapshot, ProjectId id) =>
        snapshot.Projects.Get(id)?.Name ?? snapshot.Solution.GetProject(id)?.Name ?? id.ToString();

    private static void AddPaths(SortedSet<string> paths, IEnumerable<string?> candidates)
    {
        foreach (var path in candidates.OfType<string>())
        {
            paths.Add(path);
        }
    }

    private sealed record CheckPlan(IReadOnlyList<ProjectId> Projects, IReadOnlyList<DocumentId> Documents, IReadOnlyList<string> ChangedFiles)
    {
        /// <summary>The files a change binds, before those of wholly checked projects are folded into them; analyzers run on these.</summary>
        public IReadOnlyList<DocumentId> Bound { get; init; } = Documents;
    }
}
