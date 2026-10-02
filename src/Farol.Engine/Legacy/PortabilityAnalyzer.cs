using Farol.Engine.Analysis;
using Farol.Engine.Workspaces;
using NuGet.Frameworks;

namespace Farol.Engine.Legacy;

/// <summary>A .NET Framework API a project uses that the target framework lacks, or marks obsolete.</summary>
public sealed record PortabilityIssue(string Project, ApiUsage Api, ApiAvailability Availability)
{
    public LegacyTechnology? Technology => Api.Technology;
}

/// <summary>How one project was checked: from which framework to which, how many distinct APIs, or why it was skipped.</summary>
public sealed record ProjectPortability(string Name, string FilePath, string? From, string? To, int ApisChecked, string? Skipped);

public sealed record PortabilityReport(string Target, IReadOnlyList<ProjectPortability> Projects, IReadOnlyList<PortabilityIssue> Issues, IReadOnlyList<string> Surfaces)
{
    public IEnumerable<PortabilityIssue> IssuesOf(string project) => Issues.Where(i => i.Project.Equals(project, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Which .NET Framework APIs a solution uses that a modern target does not have: every framework API the code binds to
/// is looked up in the target's reference assemblies. Projects that use WinForms or WPF are checked against the Windows
/// Desktop surface (net10.0-windows), where those run. Package APIs are left to the packages' own target frameworks.
/// </summary>
public static class PortabilityAnalyzer
{
    public static async Task<PortabilityReport> AnalyzeAsync(
        WorkspaceSnapshot snapshot, NuGetFramework target, string globalPackagesFolder, string? projectName, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(target);
        var files = snapshot.Solution.Projects
            .Where(p => p.FilePath is not null && snapshot.Projects.Get(p.Id) is not null)
            .GroupBy(p => p.FilePath!, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: snapshot.Projects.Get(g.First().Id)!.Name, FilePath: g.Key))
            .Where(p => projectName is null || p.Name.Equals(projectName.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var projects = new List<ProjectPortability>();
        var issues = new List<PortabilityIssue>();
        var surfaces = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (name, file) in files)
        {
            var primary = LegacyInventory.Primary(snapshot, file);
            var from = primary is null ? null : snapshot.Projects.Get(primary.Id)?.TargetFramework;
            if (primary is null || from is null || !TargetFrameworkNames.IsNetFramework(from))
            {
                projects.Add(new ProjectPortability(name, file, from, null, 0, from is null ? "its target framework is unknown" : $"already on {from}"));
                continue;
            }

            var compilation = await primary.GetCompilationAsync(cancellationToken);
            var desktop = compilation?.ReferencedAssemblyNames.Any(a => a.Name is "System.Windows.Forms" or "PresentationFramework") == true;
            var surface = TargetSurface.Load(target, desktop, globalPackagesFolder);
            surfaces.UnionWith(surface.Description.Split(", "));
            progress?.Report($"checking {name} ({from}) against {surface.TargetFramework}");

            var apis = (await ApiUsageScanner.ScanAsync(primary, cancellationToken)).Where(a => a.Origin == ApiOrigin.NetFramework).ToList();
            foreach (var api in apis)
            {
                var availability = surface.Check(api.Id);
                if (availability.Status != ApiStatus.Available)
                {
                    issues.Add(new PortabilityIssue(name, api, availability));
                }
            }

            projects.Add(new ProjectPortability(name, file, from, surface.TargetFramework, apis.Count, null));
        }

        return new PortabilityReport(target.GetShortFolderName(), projects, issues, [.. surfaces]);
    }
}
