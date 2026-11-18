using Farol.Engine.Packages;
using Farol.Engine.Workspaces;
using Microsoft.CodeAnalysis;

namespace Farol.Engine.Conversion;

/// <summary>What the workspace sees of one project file: its source files, references, packages, and the errors of it and its dependents.</summary>
public sealed record ProjectState(
    IReadOnlySet<string> Documents,
    IReadOnlySet<string> References,
    IReadOnlySet<string> Packages,
    IReadOnlySet<string> Errors)
{
    /// <summary>Whether the workspace had the project, with source files: an empty state proves nothing.</summary>
    public bool Loaded => Documents.Count > 0;
}

/// <summary>How a project reloaded after a conversion differs from before it: what it lost, what it gained, and new errors.</summary>
public sealed record ConversionDelta(
    IReadOnlyList<string> MissingDocuments,
    IReadOnlyList<string> AddedDocuments,
    IReadOnlyList<string> MissingReferences,
    IReadOnlyList<string> AddedReferences,
    IReadOnlyList<string> MissingPackages,
    IReadOnlyList<string> AddedPackages,
    IReadOnlyList<string> NewErrors,
    bool Loaded = true)
{
    /// <summary>The project builds the same code against the same references: nothing lost and no new error.</summary>
    public bool Same => Loaded && MissingDocuments.Count == 0 && AddedDocuments.Count == 0 && MissingReferences.Count == 0 && MissingPackages.Count == 0 && NewErrors.Count == 0;
}

/// <summary>Captures a project before and after a conversion, from the loaded workspace, and compares the two.</summary>
public static class ConversionComparison
{
    public static async Task<ProjectState> CaptureAsync(WorkspaceSnapshot snapshot, NuGetContext context, string projectFile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(context);

        var solution = snapshot.Solution;
        var variants = solution.Projects.Where(p => string.Equals(p.FilePath, projectFile, StringComparison.OrdinalIgnoreCase)).ToList();
        var directory = Path.GetDirectoryName(projectFile)!;
        var documents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in variants)
        {
            // Files the build generates under obj/ (assembly attributes) are not the project's sources.
            documents.UnionWith(project.Documents.Select(d => d.FilePath).OfType<string>()
                .Where(p => !Path.GetRelativePath(directory, p).Split(Path.DirectorySeparatorChar).Any(s => s.Equals("obj", StringComparison.OrdinalIgnoreCase)))
                .Select(p => Path.GetRelativePath(directory, p)));
            references.UnionWith(project.MetadataReferences.OfType<PortableExecutableReference>().Select(r => r.FilePath).OfType<string>().Select(Path.GetFileName).OfType<string>());
            references.UnionWith(project.ProjectReferences.Select(r => solution.GetProject(r.ProjectId)?.FilePath).OfType<string>().Select(p => "project " + Path.GetFileNameWithoutExtension(p)));
        }

        var entry = variants.Select(p => snapshot.Projects.Get(p.Id)).OfType<ProjectEntry>().FirstOrDefault();
        var packages = entry is null
            ? []
            : PackageInventory.Read(snapshot, context, entry.Name).SelectMany(p => p.Packages).Where(p => !p.IsImplicit).Select(p => $"{p.Id} {p.Version}").ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The project and everything that depends on it: a conversion that changes its output breaks them.
        var graph = solution.GetProjectDependencyGraph();
        var checkedProjects = variants.Select(p => p.Id)
            .Concat(variants.SelectMany(p => graph.GetProjectsThatTransitivelyDependOnThisProject(p.Id)))
            .Distinct()
            .ToList();
        var errors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in checkedProjects)
        {
            var project = solution.GetProject(id);
            if (project is null || await project.GetCompilationAsync(cancellationToken) is not { } compilation)
            {
                continue;
            }

            var name = ProjectNames.StripTargetFramework(project.Name);
            foreach (var diagnostic in compilation.GetDiagnostics(cancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error))
            {
                var file = diagnostic.Location.SourceTree?.FilePath is { } path ? Path.GetFileName(path) : name;
                errors.Add($"{diagnostic.Id} {name}/{file}: {diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture)}");
            }
        }

        return new ProjectState(documents, references, packages, errors);
    }

    public static ConversionDelta Compare(ProjectState before, ProjectState after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        static List<string> Except(IReadOnlySet<string> a, IReadOnlySet<string> b) => [.. a.Where(x => !b.Contains(x)).Order(StringComparer.OrdinalIgnoreCase)];
        return new ConversionDelta(
            Except(before.Documents, after.Documents),
            Except(after.Documents, before.Documents),
            Except(before.References, after.References),
            Except(after.References, before.References),
            Except(before.Packages, after.Packages),
            Except(after.Packages, before.Packages),
            Except(after.Errors, before.Errors),
            before.Loaded && after.Loaded);
    }
}
