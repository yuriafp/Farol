using Farol.Core.Paths;
using Farol.Core.Text;
using Farol.Engine.Packages;
using Farol.Engine.Workspaces;
using Farol.Tools.Infrastructure;
using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace Farol.Tools.Features.Packages;

/// <summary>
/// Packages for agents: the counts that matter first, then per project the direct packages and every package with a
/// problem on its own line, and the unremarkable transitive ones folded into one line.
/// </summary>
internal static class PackagesRenderer
{
    private const int FoldedListChars = 1200;

    public static PackagesOutput Render(
        ResponseBuilder text, WorkspaceTarget target, IReadOnlyList<ProjectPackages> projects, FeedCheck check, PackageFilter filter, bool offline, string root)
    {
        var shownProjects = new List<PackageProjectItem>();
        var view = new View(check, projects);
        var versions = projects.SelectMany(p => p.Packages).Select(p => p.Identity).Distinct().ToList();
        var direct = projects.SelectMany(p => p.Packages).Where(p => p.IsDirect).Select(p => p.Identity).Distinct().ToList();
        text.Line(
            $"packages: {target.DisplayName} · {projects.Count(p => p.Packages.Count > 0)} of {projects.Count} project(s) use packages · " +
            $"{versions.Count} package version(s): {versions.Count(v => view.Advisories(v).Count > 0)} vulnerable, " +
            $"{versions.Count(v => view.Deprecation(v) is not null)} deprecated, {direct.Count(v => view.Newer(v) is not null)} direct outdated");

        if (offline)
        {
            text.Line("feeds: not checked (--offline): vulnerabilities come from the last restore's audit; deprecation and newer versions are unknown");
        }
        else if (versions.Count > 0)
        {
            text.Line($"feeds: {string.Join(", ", check.Sources)}{(check.HasVulnerabilityData ? string.Empty : " · none of them provides vulnerability data: vulnerabilities come from the last restore's audit")}");
            foreach (var problem in check.Problems)
            {
                text.Line($"feed problem: {problem}");
            }
        }

        if (!filter.IsAll)
        {
            text.Line($"include: {filter}");
        }

        PackagesOutput Output(int omitted, IReadOnlyList<string> withoutPackages) => new(
            target.DisplayName,
            projects.Count,
            projects.Count(p => p.Packages.Count > 0),
            versions.Count,
            versions.Count(v => view.Advisories(v).Count > 0),
            versions.Count(v => view.Deprecation(v) is not null),
            direct.Count(v => view.Newer(v) is not null),
            !offline,
            offline ? [] : check.Sources,
            offline ? [] : check.Problems,
            filter.ToString(),
            shownProjects,
            omitted,
            withoutPackages);

        var withoutPackages = new List<string>();
        var shown = 0;
        for (var index = 0; index < projects.Count; index++)
        {
            var project = projects[index];
            if (project.Packages.Count == 0 && project.Problem is null)
            {
                withoutPackages.Add(project.Name);
                continue;
            }

            var matching = project.Packages.Where(p => Matches(p, filter, view)).ToList();
            if (matching.Count == 0 && !filter.IsAll)
            {
                continue;
            }

            if (!RenderProject(text, project, matching, filter, view, root, shownProjects))
            {
                text.More(projects.Count - index, "raise maxTokens or pass project");
                return Output(projects.Count - index, filter.IsAll ? withoutPackages : []);
            }

            shown++;
        }

        if (filter.IsAll && withoutPackages.Count > 0)
        {
            text.TryLine($"no packages: {string.Join(", ", withoutPackages)}");
        }

        if (!filter.IsAll && shown == 0)
        {
            text.Line($"no package matches include='{filter}'.");
        }

        return Output(0, filter.IsAll ? withoutPackages : []);
    }

    private static bool RenderProject(
        ResponseBuilder text, ProjectPackages project, List<InstalledPackage> packages, PackageFilter filter, View view, string root, List<PackageProjectItem> shownProjects)
    {
        var style = project.Style switch
        {
            PackageStyle.PackagesConfig => "packages.config",
            PackageStyle.PackageReference when project.CentralPackageManagement && project.VersionsFile is { } props => $"versions in {DisplayPath.From(root, props)}",
            _ => "PackageReference",
        };
        var counts = project.Style == PackageStyle.PackagesConfig
            ? $"{project.Packages.Count} package(s)"
            : $"{project.Packages.Count(p => p.IsDirect)} direct, {project.Packages.Count(p => !p.IsDirect)} transitive";
        var frameworks = project.Frameworks.Count > 0 ? $" ({string.Join(", ", project.Frameworks)})" : string.Empty;
        if (!text.TryLine($"{project.Name}{frameworks} · {style} · {counts}"))
        {
            return false;
        }

        var items = new List<PackageItem>();
        var item = new PackageProjectItem(
            project.Name,
            project.Frameworks,
            project.Style == PackageStyle.PackagesConfig ? "packages.config" : "PackageReference",
            project.CentralPackageManagement && project.VersionsFile is { } versionsFile ? DisplayPath.From(root, versionsFile) : null,
            project.Packages.Count(p => p.IsDirect || project.Style == PackageStyle.PackagesConfig),
            project.Style == PackageStyle.PackagesConfig ? 0 : project.Packages.Count(p => !p.IsDirect),
            project.Problem,
            items,
            0,
            0);
        shownProjects.Add(item);

        if (project.Problem is not null && !text.TryLine($"  note: {project.Problem}"))
        {
            shownProjects[^1] = item with { Omitted = packages.Count };
            return false;
        }

        // Every package gets a line when filtering or in packages.config (which lists them all); otherwise unremarkable
        // transitive packages fold into one.
        var ownLine = packages.Where(p => !filter.IsAll || p.IsDirect || project.Style == PackageStyle.PackagesConfig || view.HasProblem(p.Identity)).ToList();
        var folded = packages.Except(ownLine).ToList();
        foreach (var package in ownLine)
        {
            if (!text.TryLine("- " + Describe(package, project, filter, view)))
            {
                var left = ownLine.Count - ownLine.IndexOf(package) + folded.Count;
                text.More(left, "raise maxTokens or pass project");
                shownProjects[^1] = item with { Omitted = left };
                return false;
            }

            items.Add(Item(package, project, filter, view));
        }

        shownProjects[^1] = item with { FoldedTransitive = folded.Count };

        if (folded.Count > 0)
        {
            var multiTargeted = project.Frameworks.Count > 1;
            var names = folded.Select(p => $"{p.Id} {p.Version.ToNormalizedString()}{(multiTargeted && p.Frameworks.Count < project.Frameworks.Count ? $" ({string.Join(", ", p.Frameworks)})" : string.Empty)}");
            return text.TryLine($"  other transitive ({folded.Count}): {Fold(names)}");
        }

        return true;
    }

    private static string Describe(InstalledPackage package, ProjectPackages project, PackageFilter filter, View view)
    {
        var head = package.IsDirect || project.Style == PackageStyle.PackagesConfig
            ? $"{package.Id} {package.Version.ToNormalizedString()}"
            : $"transitive {package.Id} {package.Version.ToNormalizedString()}";
        if (package.IsImplicit)
        {
            head += " (implicit)";
        }

        if (project.Style == PackageStyle.PackageReference && package.Requested is { HasLowerBound: true, MinVersion: { } requested } && requested != package.Version)
        {
            head += $" (requested {requested.ToNormalizedString()})";
        }

        var parts = new List<string>();
        if (project.Frameworks.Count > 1 && package.Frameworks.Count > 0 && package.Frameworks.Count < project.Frameworks.Count)
        {
            parts.Add(string.Join(", ", package.Frameworks));
        }

        if (view.Advisories(package.Identity) is { Count: > 0 } advisories)
        {
            parts.Add("vulnerable: " + string.Join(", ", advisories.Select(a => $"{a.Severity} {a.Name}")));
        }

        if (view.Deprecation(package.Identity) is { } deprecation)
        {
            parts.Add($"deprecated ({string.Join(", ", deprecation.Reasons)}{(deprecation.AlternatePackage is { } alternative ? $"; use {alternative}" : string.Empty)})");
        }

        if ((package.IsDirect || filter.Outdated) && view.Newer(package.Identity) is { } newer)
        {
            parts.Add($"latest {newer.ToNormalizedString()}");
        }

        if (package.Via.Count > 0)
        {
            parts.Add(project.Style == PackageStyle.PackagesConfig ? $"required by {string.Join(", ", package.Via)}" : $"via {string.Join(" > ", package.Via)}");
        }

        return parts.Count == 0 ? head : $"{head} · {string.Join(" · ", parts)}";
    }

    private static PackageItem Item(InstalledPackage package, ProjectPackages project, PackageFilter filter, View view) => new(
        package.Id,
        package.Version.ToNormalizedString(),
        package.IsDirect || project.Style == PackageStyle.PackagesConfig,
        package.IsImplicit,
        project.Style == PackageStyle.PackageReference && package.Requested is { HasLowerBound: true, MinVersion: { } requested } && requested != package.Version
            ? requested.ToNormalizedString()
            : null,
        package.Frameworks,
        [.. view.Advisories(package.Identity).Select(a => new AdvisoryItem(a.Severity, a.Name, a.Url.ToString()))],
        view.Deprecation(package.Identity) is { } deprecation ? new DeprecationItem(deprecation.Reasons, deprecation.AlternatePackage) : null,
        (package.IsDirect || filter.Outdated) && view.Newer(package.Identity) is { } newer ? newer.ToNormalizedString() : null,
        package.Via);

    private static bool Matches(InstalledPackage package, PackageFilter filter, View view)
    {
        if (filter.DirectOnly && !package.IsDirect)
        {
            return false;
        }

        return !filter.AnyProblem
            || (filter.Vulnerable && view.Advisories(package.Identity).Count > 0)
            || (filter.Deprecated && view.Deprecation(package.Identity) is not null)
            || (filter.Outdated && view.Newer(package.Identity) is not null);
    }

    private static string Fold(IEnumerable<string> names)
    {
        var list = names.ToList();
        var written = new List<string>();
        var length = 0;
        foreach (var name in list)
        {
            if (length + name.Length + 2 > FoldedListChars)
            {
                return $"{string.Join(", ", written)}, … {list.Count - written.Count} more (include=all with project=… lists them)";
            }

            written.Add(name);
            length += name.Length + 2;
        }

        return string.Join(", ", written);
    }

    /// <summary>
    /// What is known about each package version: the feeds' answer, or, when no feed provides vulnerability data
    /// (offline, or feeds without it), the vulnerabilities restore recorded in the assets files.
    /// </summary>
    private sealed class View(FeedCheck check, IReadOnlyList<ProjectPackages> projects)
    {
        private readonly Dictionary<PackageIdentity, List<PackageAdvisory>> _restoreAudit = projects
            .SelectMany(p => p.RestoreAudit)
            .Where(f => f.AdvisoryUrl is not null)
            .GroupBy(f => new PackageIdentity(f.Id, f.Version))
            .ToDictionary(g => g.Key, g => g.Select(f => new PackageAdvisory(f.Severity, f.AdvisoryUrl!)).DistinctBy(a => a.Url).ToList());

        public IReadOnlyList<PackageAdvisory> Advisories(PackageIdentity identity) =>
            check.HasVulnerabilityData ? check.StatusOf(identity).Vulnerabilities : _restoreAudit.GetValueOrDefault(identity) ?? [];

        public PackageDeprecation? Deprecation(PackageIdentity identity) => check.StatusOf(identity).Deprecation;

        public NuGetVersion? Newer(PackageIdentity identity) => check.StatusOf(identity).Newer(identity.Version);

        public bool HasProblem(PackageIdentity identity) => Advisories(identity).Count > 0 || Deprecation(identity) is not null;
    }
}

/// <summary>Structured output of dotnet_packages: the projects and packages the text lists; folded transitive packages are only counted.</summary>
public sealed record PackagesOutput(
    string Workspace,
    int Projects,
    int ProjectsWithPackages,
    int PackageVersions,
    int Vulnerable,
    int Deprecated,
    int DirectOutdated,
    bool FeedsChecked,
    IReadOnlyList<string> Feeds,
    IReadOnlyList<string> FeedProblems,
    string Include,
    IReadOnlyList<PackageProjectItem> ProjectList,
    int OmittedProjects,
    IReadOnlyList<string> ProjectsWithoutPackages);

public sealed record PackageProjectItem(
    string Name,
    IReadOnlyList<string> TargetFrameworks,
    string Style,
    string? VersionsFile,
    int Direct,
    int Transitive,
    string? Note,
    IReadOnlyList<PackageItem> Packages,
    int FoldedTransitive,
    int Omitted);

public sealed record PackageItem(
    string Id,
    string Version,
    bool Direct,
    bool Implicit,
    string? Requested,
    IReadOnlyList<string> TargetFrameworks,
    IReadOnlyList<AdvisoryItem> Vulnerabilities,
    DeprecationItem? Deprecation,
    string? Latest,
    IReadOnlyList<string> Via);

public sealed record AdvisoryItem(string Severity, string Name, string Url);

public sealed record DeprecationItem(IReadOnlyList<string> Reasons, string? Alternative);
