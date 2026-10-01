using System.Collections.Concurrent;
using System.Globalization;
using Farol.Core;
using Farol.Engine.Workspaces;
using Microsoft.CodeAnalysis;

namespace Farol.Engine.Diagnostics;

/// <summary>
/// dotnet_check: compiles what an edit can affect and reports the diagnostics the load baseline does not have.
/// Edits inside member bodies re-check only the edited files; declaration changes re-check the project and every
/// project that depends on it, in either language, so a C# signature change surfaces as the VB error it causes.
/// </summary>
public static class DiagnosticCheck
{
    public static async Task<CheckResult> RunAsync(WorkspaceSnapshot snapshot, CheckRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(request);

        var plan = request.Scope switch
        {
            CheckScope.Changed => await PlanChangedAsync(snapshot, request.IncludeExisting, cancellationToken),
            CheckScope.File => PlanFile(snapshot, request.FilePath),
            CheckScope.Project => PlanProject(snapshot, request.Project),
            _ => new CheckPlan([.. snapshot.Solution.ProjectIds], [], []),
        };

        var solution = snapshot.Solution;
        var baseline = DiagnosticBaseline.For(snapshot.Load.Solution);
        var found = new ConcurrentBag<(ProjectId Project, List<Diagnostic> New, List<Diagnostic> Existing)>();
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(plan.Projects, options, async (id, token) =>
        {
            var current = await CompilerDiagnostics.ForProjectAsync(solution.GetProject(id)!, token);
            var (fresh, existing) = (await baseline.ProjectAsync(id, token)).Split(current);
            found.Add((id, fresh, existing));
        });

        await Parallel.ForEachAsync(plan.Documents, options, async (id, token) =>
        {
            var current = await CompilerDiagnostics.ForDocumentAsync(solution.GetDocument(id)!, token);
            var (fresh, existing) = (await baseline.DocumentAsync(id, token)).Split(current);
            found.Add((id.ProjectId, fresh, existing));
        });

        return new CheckResult(
            Merge(snapshot, found.SelectMany(f => f.New.Select(d => (f.Project, d)))),
            request.IncludeExisting ? Merge(snapshot, found.SelectMany(f => f.Existing.Select(d => (f.Project, d)))) : [],
            plan.ChangedFiles,
            [.. plan.Projects.Select(id => ProjectName(snapshot, id)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)],
            [.. plan.Documents.Select(id => solution.GetDocument(id)?.FilePath).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)]);
    }

    private static async Task<CheckPlan> PlanChangedAsync(WorkspaceSnapshot snapshot, bool includeExisting, CancellationToken cancellationToken)
    {
        var loaded = snapshot.Load.Solution;
        var current = snapshot.Solution;
        var changedFiles = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var declarationChanges = new HashSet<ProjectId>();
        var bodyChanges = new Dictionary<ProjectId, List<DocumentId>>();
        foreach (var project in current.GetChanges(loaded).GetProjectChanges())
        {
            var added = project.GetAddedDocuments().ToList();
            var removed = project.GetRemovedDocuments().ToList();
            var changed = project.GetChangedDocuments(onlyGetDocumentsWithTextChanges: true).ToList();
            AddPaths(changedFiles, added.Select(id => current.GetDocument(id)?.FilePath));
            AddPaths(changedFiles, removed.Select(id => loaded.GetDocument(id)?.FilePath));
            AddPaths(changedFiles, changed.Select(id => current.GetDocument(id)?.FilePath));

            var declarationsChanged = added.Count > 0 || removed.Count > 0;
            foreach (var id in changed)
            {
                if (declarationsChanged)
                {
                    break;
                }

                declarationsChanged = !await SameDeclarationsAsync(loaded.GetDocument(id)!, current.GetDocument(id)!, cancellationToken);
            }

            if (declarationsChanged)
            {
                declarationChanges.Add(project.ProjectId);
            }
            else if (changed.Count > 0)
            {
                bodyChanges[project.ProjectId] = changed;
            }
        }

        var graph = current.GetProjectDependencyGraph();
        var projects = new HashSet<ProjectId>(declarationChanges);
        foreach (var id in declarationChanges)
        {
            projects.UnionWith(graph.GetProjectsThatTransitivelyDependOnThisProject(id));
        }

        // Existing diagnostics are listed per project, so the projects with body edits are checked whole too.
        if (includeExisting)
        {
            projects.UnionWith(bodyChanges.Keys);
        }

        var documents = bodyChanges.Where(kv => !projects.Contains(kv.Key)).SelectMany(kv => kv.Value).ToList();
        return new CheckPlan([.. projects], documents, [.. changedFiles]);
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
            throw new FarolException(ErrorCodes.InvalidArgument, "scope='file' needs 'path'.", "Pass the source file, relative to the workspace root.");
        }

        var ids = snapshot.Solution.GetDocumentIdsWithFilePath(filePath);
        if (ids.IsEmpty)
        {
            throw new FarolException(
                ErrorCodes.InvalidArgument,
                $"'{Path.GetFileName(filePath)}' is not a source file of the workspace.",
                "Use a path relative to the workspace root, as returned by other dotnet_* tools.");
        }

        return new CheckPlan([], [.. ids], []);
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

    private sealed record CheckPlan(IReadOnlyList<ProjectId> Projects, IReadOnlyList<DocumentId> Documents, IReadOnlyList<string> ChangedFiles);
}
