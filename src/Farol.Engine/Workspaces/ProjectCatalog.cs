using Microsoft.CodeAnalysis;

namespace Farol.Engine.Workspaces;

/// <summary>A Roslyn project as agents see it: display name, project file and the target framework of this variant.</summary>
public sealed record ProjectEntry(ProjectId Id, string Name, string FilePath, string? TargetFramework, bool IsSdkStyle);

/// <summary>
/// Maps Roslyn projects (one per target framework) back to project files. Built once per load: project
/// ids survive source edits, so the catalog stays valid for every snapshot until the next reload.
/// </summary>
public sealed class ProjectCatalog
{
    private readonly Dictionary<ProjectId, ProjectEntry> _entries;
    private readonly List<(string Directory, bool IsSdkStyle, List<ProjectId> Ids)> _directories;
    private readonly Dictionary<string, int> _variantsPerFile;

    private ProjectCatalog(Dictionary<ProjectId, ProjectEntry> entries)
    {
        _entries = entries;
        _directories = entries.Values
            .GroupBy(e => Path.GetDirectoryName(e.FilePath)!, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.First().IsSdkStyle, g.Select(e => e.Id).ToList()))
            .OrderByDescending(d => d.Key.Length)
            .ToList();
        _variantsPerFile = entries.Values
            .GroupBy(e => e.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<string> ProjectDirectories => [.. _directories.Select(d => d.Directory)];

    /// <summary>Distinct project names, as agents pass them in a <c>project</c> argument.</summary>
    public IReadOnlyList<string> Names => [.. _entries.Values.Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Every target-framework variant of the project with this name (case-insensitive).</summary>
    public IReadOnlyList<ProjectEntry> FindByName(string name) =>
        [.. _entries.Values.Where(e => e.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))];

    /// <summary>True when the project file is loaded once per target framework.</summary>
    public bool IsMultiTargeted(ProjectId id) =>
        Get(id) is { } entry && _variantsPerFile.TryGetValue(entry.FilePath, out var count) && count > 1;

    public static ProjectCatalog Build(Solution solution, LoadReport report)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(report);

        var facts = new Dictionary<string, ProjectFileFacts>(StringComparer.OrdinalIgnoreCase);
        var entries = new Dictionary<ProjectId, ProjectEntry>();
        foreach (var project in solution.Projects.Where(p => p.FilePath is not null))
        {
            var path = project.FilePath!;
            if (!facts.TryGetValue(path, out var fact))
            {
                fact = ProjectFileInspector.Inspect(path);
                facts[path] = fact;
            }

            entries[project.Id] = new ProjectEntry(
                project.Id,
                ProjectNames.StripTargetFramework(project.Name),
                path,
                ProjectNames.TargetFrameworkSuffix(project.Name) ?? SingleFramework(path, report, fact),
                fact.IsSdkStyle);
        }

        return new ProjectCatalog(entries);
    }

    public ProjectEntry? Get(ProjectId? id) => id is not null && _entries.TryGetValue(id, out var entry) ? entry : null;

    /// <summary>
    /// The projects a new source file belongs to without editing a project file: every target-framework
    /// variant of the nearest SDK-style project whose directory contains it (classic projects list files explicitly).
    /// </summary>
    public IReadOnlyList<ProjectId> SdkProjectsContaining(string filePath) =>
        Nearest(filePath) is { IsSdkStyle: true } nearest ? nearest.Ids : [];

    /// <summary>Every target-framework variant of the nearest project whose directory contains the file, whatever its format.</summary>
    public IReadOnlyList<ProjectId> ProjectsContaining(string filePath) =>
        Nearest(filePath)?.Ids ?? [];

    // Directories are ordered longest first, so the first match is the nearest (innermost) project.
    private (string Directory, bool IsSdkStyle, List<ProjectId> Ids)? Nearest(string filePath)
    {
        foreach (var entry in _directories)
        {
            if (filePath.StartsWith(entry.Directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    private static string? SingleFramework(string path, LoadReport report, ProjectFileFacts facts)
    {
        if (report.TargetFrameworks.TryGetValue(path, out var loaded) && loaded.Count == 1)
        {
            return loaded[0];
        }

        return facts.DeclaredTargetFrameworks.Count == 1 ? facts.DeclaredTargetFrameworks[0] : null;
    }
}

/// <summary>MSBuildWorkspace names multi-targeted projects "Name(tfm)".</summary>
public static class ProjectNames
{
    public static string StripTargetFramework(string name) =>
        TargetFrameworkSuffix(name) is null ? name : name[..name.LastIndexOf('(')];

    public static string? TargetFrameworkSuffix(string name)
    {
        var open = name.LastIndexOf('(');
        return open > 0 && name.EndsWith(')') ? name[(open + 1)..^1] : null;
    }
}
