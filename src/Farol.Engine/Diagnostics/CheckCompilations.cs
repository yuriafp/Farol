using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

namespace Farol.Engine.Diagnostics;

/// <summary>
/// The compilations dotnet_check binds files against. A project edited since the load uses the workspace's current
/// compilation. A project with no edits of its own whose references changed would be rebuilt by the workspace, source
/// generators rerun, which takes seconds for a large project although neither its sources nor, as a rule, its generated
/// files changed: instead its load-time compilation, generated files included, is pointed at the new compilations of
/// its references. Projects nothing changed under keep their load-time compilation.
/// </summary>
internal sealed class CheckCompilations
{
    private static readonly ConditionalWeakTable<Solution, CheckCompilations> Instances = new();

    private readonly Solution _loaded;
    private readonly Solution _current;
    private readonly HashSet<ProjectId> _edited;
    private readonly HashSet<ProjectId> _affected;
    private readonly ConcurrentDictionary<ProjectId, Lazy<Task<Compilation?>>> _compilations = new();

    private CheckCompilations(Solution loaded, Solution current)
    {
        _loaded = loaded;
        _current = current;
        _edited = [.. current.GetChanges(loaded).GetProjectChanges().Select(p => p.ProjectId)];
        var graph = current.GetProjectDependencyGraph();
        _affected = [.. _edited, .. _edited.SelectMany(graph.GetProjectsThatTransitivelyDependOnThisProject)];
    }

    public static CheckCompilations For(Solution loaded, Solution current)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(current);
        var instance = Instances.GetValue(current, c => new CheckCompilations(loaded, c));
        return ReferenceEquals(instance._loaded, loaded) ? instance : new CheckCompilations(loaded, current);
    }

    /// <summary>Whether the project's own files changed since the load: its files are read from the current solution.</summary>
    public bool IsEdited(ProjectId project) => _edited.Contains(project);

    // Shared by the requests of one snapshot; computed without the caller's token so a cancelled request cannot poison it.
    public Task<Compilation?> GetAsync(ProjectId project, CancellationToken cancellationToken) =>
        _compilations.GetOrAdd(project, id => new Lazy<Task<Compilation?>>(() => Task.Run(() => ComputeAsync(id)))).Value.WaitAsync(cancellationToken);

    private async Task<Compilation?> ComputeAsync(ProjectId id)
    {
        if (_edited.Contains(id) || _loaded.GetProject(id) is not { } atLoad)
        {
            return _current.GetProject(id) is { } project ? await project.GetCompilationAsync() : null;
        }

        var compilation = await atLoad.GetCompilationAsync();
        if (compilation is null || !_affected.Contains(id))
        {
            return compilation;
        }

        // A reference to a project in another language is a metadata image the workspace emits: rebuild that one its way.
        if (atLoad.ProjectReferences.Any(r => _affected.Contains(r.ProjectId) && _loaded.GetProject(r.ProjectId)?.Language != atLoad.Language))
        {
            return await _current.GetProject(id)!.GetCompilationAsync();
        }

        foreach (var reference in compilation.References.OfType<CompilationReference>().ToList())
        {
            if (_loaded.GetProject(reference.Compilation.Assembly)?.Id is { } referenced && _affected.Contains(referenced)
                && await ComputeReferencedAsync(referenced) is { } replacement)
            {
                compilation = compilation.ReplaceReference(reference, replacement.ToMetadataReference(reference.Properties.Aliases, reference.Properties.EmbedInteropTypes));
            }
        }

        return compilation;
    }

    private Task<Compilation?> ComputeReferencedAsync(ProjectId id) =>
        _compilations.GetOrAdd(id, key => new Lazy<Task<Compilation?>>(() => Task.Run(() => ComputeAsync(key)))).Value;
}
