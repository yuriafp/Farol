using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Farol.Engine.Workspaces;
using Microsoft.CodeAnalysis;
using NuGet.Common;
using NuGet.Frameworks;
using NuGet.LibraryModel;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.ProjectModel;
using NuGet.Versioning;

namespace Farol.Engine.Packages;

/// <summary>
/// The packages each project uses, read from what is on disk: restore's assets file (obj/project.assets.json) for
/// PackageReference projects, packages.config for classic ones. No network, no MSBuild evaluation.
/// </summary>
public static partial class PackageInventory
{
    private const string AssetsFileName = "project.assets.json";
    private const string PackageLibrary = "package";
    private const string ProjectLibrary = "project";

    public static IReadOnlyList<ProjectPackages> Read(WorkspaceSnapshot snapshot, NuGetContext context, string? projectName)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(context);

        var projects = new List<ProjectPackages>();
        var entries = snapshot.Solution.Projects
            .Select(p => (Project: p, Entry: snapshot.Projects.Get(p.Id)))
            .Where(p => p.Entry is not null && (projectName is null || p.Entry.Name.Equals(projectName.Trim(), StringComparison.OrdinalIgnoreCase)))
            .GroupBy(p => p.Entry!.FilePath, StringComparer.OrdinalIgnoreCase);

        foreach (var file in entries)
        {
            var name = file.First().Entry!.Name;
            var frameworks = file.Select(p => p.Entry!.TargetFramework).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
            projects.Add(ReadProject(context, name, file.Key, frameworks, file.Select(p => p.Project)));
        }

        return [.. projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
    }

    internal static ProjectPackages ReadProject(NuGetContext context, string name, string projectPath, IReadOnlyList<string> frameworks, IEnumerable<Project> variants)
    {
        var directory = Path.GetDirectoryName(projectPath)!;
        var packagesConfig = Path.Combine(directory, "packages.config");
        if (File.Exists(packagesConfig))
        {
            return ReadPackagesConfig(context, name, projectPath, packagesConfig, frameworks);
        }

        if (FindAssetsFile(directory, variants) is { } assets)
        {
            return ReadAssetsFile(name, projectPath, assets, frameworks);
        }

        return ReadUnrestored(name, projectPath, frameworks);
    }

    /// <summary>
    /// The warnings and errors restore recorded in a project's assets file. Every build replays them, design-time
    /// builds included.
    /// </summary>
    public static IReadOnlyList<RestoreMessage> RestoreMessages(string projectPath, IEnumerable<Project> variants)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentNullException.ThrowIfNull(variants);
        if (FindAssetsFile(Path.GetDirectoryName(projectPath)!, variants) is not { } assets)
        {
            return [];
        }

        try
        {
            return LockFileUtilities.GetLockFile(assets, NullLogger.Instance)?.LogMessages
                .Where(m => !string.IsNullOrWhiteSpace(m.Message))
                .Select(m => new RestoreMessage(projectPath, m.Code.ToString(), m.Level >= LogLevel.Error, m.Message.Trim()))
                .ToList() ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return [];
        }
    }

    /// <summary>
    /// restore writes the assets file next to the intermediate output (obj/, or artifacts/obj/Name/): walk up from the
    /// intermediate assembly Roslyn knows, then fall back to the default obj/ folder.
    /// </summary>
    private static string? FindAssetsFile(string projectDirectory, IEnumerable<Project> variants)
    {
        foreach (var assembly in variants.Select(v => v.CompilationOutputInfo.AssemblyPath).OfType<string>())
        {
            var folder = Path.GetDirectoryName(assembly);
            for (var depth = 0; folder is not null && depth < 4; depth++, folder = Path.GetDirectoryName(folder))
            {
                var candidate = Path.Combine(folder, AssetsFileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        var fallback = Path.Combine(projectDirectory, "obj", AssetsFileName);
        return File.Exists(fallback) ? fallback : null;
    }

    private static ProjectPackages ReadAssetsFile(string name, string projectPath, string assetsPath, IReadOnlyList<string> frameworks)
    {
        var lockFile = LockFileUtilities.GetLockFile(assetsPath, NullLogger.Instance);
        if (lockFile?.PackageSpec is null)
        {
            return ReadUnrestored(name, projectPath, frameworks) with { Problem = "the assets file could not be read: run dotnet restore" };
        }

        var central = lockFile.PackageSpec.RestoreMetadata?.CentralPackageVersionsEnabled == true;
        var found = new Dictionary<(string Id, NuGetVersion Version, bool Direct), (InstalledPackage Package, List<string> Frameworks)>();
        foreach (var target in lockFile.Targets.Where(t => string.IsNullOrEmpty(t.RuntimeIdentifier)))
        {
            var info = lockFile.PackageSpec.TargetFrameworks.FirstOrDefault(f => f.FrameworkName.Equals(target.TargetFramework));
            var alias = string.IsNullOrEmpty(info?.TargetAlias) ? target.TargetFramework.GetShortFolderName() : info.TargetAlias;
            var dependencies = info?.Dependencies.Where(d => d.LibraryRange.TypeConstraintAllows(LibraryDependencyTarget.Package)).ToList() ?? [];
            var libraries = target.Libraries.Where(l => l.Name is not null && l.Version is not null).ToDictionary(l => l.Name!, StringComparer.OrdinalIgnoreCase);
            var chains = Chains(libraries, dependencies.Select(d => d.Name));

            foreach (var library in libraries.Values.Where(l => l.Type == PackageLibrary))
            {
                var direct = dependencies.FirstOrDefault(d => d.Name.Equals(library.Name, StringComparison.OrdinalIgnoreCase));
                var key = (library.Name!, library.Version!, direct is not null);
                if (!found.TryGetValue(key, out var entry))
                {
                    var package = new InstalledPackage(
                        library.Name!,
                        library.Version!,
                        IsDirect: direct is not null,
                        direct?.LibraryRange.VersionRange,
                        [],
                        direct is null ? chains.GetValueOrDefault(library.Name!) ?? [] : [],
                        IsImplicit: direct?.AutoReferenced == true);
                    entry = (package, []);
                    found[key] = entry;
                }

                entry.Frameworks.Add(alias);
            }
        }

        var packages = found.Values
            .Select(e => e.Package with { Frameworks = [.. e.Frameworks.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)] })
            .OrderBy(p => p.IsDirect ? 0 : 1)
            .ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Version)
            .ToList();

        var stale = File.GetLastWriteTimeUtc(projectPath) > File.GetLastWriteTimeUtc(assetsPath)
            || (central && CentralVersionsFile(projectPath) is { } props && File.GetLastWriteTimeUtc(props) > File.GetLastWriteTimeUtc(assetsPath));
        return new ProjectPackages(
            name,
            projectPath,
            PackageStyle.PackageReference,
            frameworks,
            packages,
            central ? CentralVersionsFile(projectPath) : projectPath,
            central,
            stale ? "package references changed since the last restore: run dotnet restore for current versions" : null,
            RestoreAudit(lockFile, packages));
    }

    /// <summary>
    /// Breadth-first from the direct packages and referenced projects: the first chain that reaches a package is the
    /// shortest, which is what an agent needs to know which direct reference to upgrade.
    /// </summary>
    private static Dictionary<string, IReadOnlyList<string>> Chains(Dictionary<string, LockFileTargetLibrary> libraries, IEnumerable<string> directPackages)
    {
        var chains = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Name, List<string> Path)>();
        foreach (var root in directPackages.Concat(libraries.Values.Where(l => l.Type == ProjectLibrary).Select(l => l.Name!)))
        {
            if (libraries.ContainsKey(root) && chains.TryAdd(root, []))
            {
                queue.Enqueue((root, [root]));
            }
        }

        while (queue.Count > 0)
        {
            var (current, path) = queue.Dequeue();
            foreach (var dependency in libraries[current].Dependencies)
            {
                if (libraries.ContainsKey(dependency.Id) && chains.TryAdd(dependency.Id, path))
                {
                    queue.Enqueue((dependency.Id, [.. path, dependency.Id]));
                }
            }
        }

        return chains;
    }

    /// <summary>
    /// NU1901–NU1904 entries restore left in the assets file. The message is localized, so only the code (severity),
    /// the library id and the advisory URL are read.
    /// </summary>
    private static List<RestoreAuditFinding> RestoreAudit(LockFile lockFile, List<InstalledPackage> packages)
    {
        var findings = new List<RestoreAuditFinding>();
        foreach (var message in lockFile.LogMessages)
        {
            var severity = message.Code switch
            {
                NuGetLogCode.NU1901 => "low",
                NuGetLogCode.NU1902 => "moderate",
                NuGetLogCode.NU1903 => "high",
                NuGetLogCode.NU1904 => "critical",
                _ => null,
            };
            if (severity is null || message.LibraryId is null)
            {
                continue;
            }

            var url = AdvisoryUrl().Match(message.Message ?? string.Empty) is { Success: true } match && Uri.TryCreate(match.Value, UriKind.Absolute, out var parsed) ? parsed : null;
            foreach (var version in packages.Where(p => p.Id.Equals(message.LibraryId, StringComparison.OrdinalIgnoreCase)).Select(p => p.Version).Distinct())
            {
                findings.Add(new RestoreAuditFinding(message.LibraryId, version, severity, url));
            }
        }

        return [.. findings.Distinct()];
    }

    private static ProjectPackages ReadPackagesConfig(NuGetContext context, string name, string projectPath, string packagesConfig, IReadOnlyList<string> frameworks)
    {
        List<PackageReference> entries;
        try
        {
            entries = [.. new PackagesConfigReader(XDocument.Load(packagesConfig)).GetPackages(allowDuplicatePackageIds: true)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or PackagesConfigReaderException)
        {
            return new ProjectPackages(name, projectPath, PackageStyle.PackagesConfig, frameworks, [], packagesConfig, false, $"packages.config could not be read: {ex.Message}", []);
        }

        // Every package is listed explicitly; the ones another installed package depends on are what restore would
        // call transitive. Their dependencies come from the restored packages' manifests.
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var projectFramework = frameworks.Count > 0 ? NuGetFramework.Parse(frameworks[0]) : NuGetFramework.AnyFramework;
        var requiredBy = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var unresolved = 0;
        foreach (var entry in entries)
        {
            var nuspec = ReadNuspec(context, projectDirectory, entry.PackageIdentity);
            if (nuspec is null)
            {
                unresolved++;
                continue;
            }

            var framework = entry.TargetFramework is { IsUnsupported: false } declared ? declared : projectFramework;
            var group = NuGetFrameworkUtility.GetNearest(nuspec.GetDependencyGroups(), framework, g => g.TargetFramework);
            foreach (var dependency in group?.Packages ?? [])
            {
                if (entries.Any(e => e.PackageIdentity.Id.Equals(dependency.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    (requiredBy.TryGetValue(dependency.Id, out var list) ? list : requiredBy[dependency.Id] = []).Add(entry.PackageIdentity.Id);
                }
            }
        }

        var packages = entries
            .Select(e =>
            {
                var parents = requiredBy.GetValueOrDefault(e.PackageIdentity.Id) ?? [];
                var tfm = e.TargetFramework is { IsUnsupported: false } f ? f.GetShortFolderName() : null;
                return new InstalledPackage(
                    e.PackageIdentity.Id,
                    e.PackageIdentity.Version,
                    IsDirect: parents.Count == 0,
                    e.AllowedVersions,
                    tfm is null ? [] : [tfm],
                    [.. parents.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)],
                    IsImplicit: false);
            })
            .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var problem = unresolved == 0 ? null : $"{unresolved} of {entries.Count} package(s) not restored, so the dependencies between packages are partly unknown (dotnet_build restores them)";
        return new ProjectPackages(name, projectPath, PackageStyle.PackagesConfig, frameworks, packages, packagesConfig, false, problem, []);
    }

    private static NuspecReader? ReadNuspec(NuGetContext context, string projectDirectory, PackageIdentity identity)
    {
        try
        {
            if (context.FindInstalled(identity.Id, identity.Version) is { } installed)
            {
                var manifest = Directory.EnumerateFiles(installed, "*.nuspec").FirstOrDefault();
                if (manifest is not null)
                {
                    return new NuspecReader(manifest);
                }
            }

            if (context.FindInRepository(projectDirectory, identity) is { } folder)
            {
                var manifest = Directory.EnumerateFiles(folder, "*.nuspec").FirstOrDefault();
                if (manifest is not null)
                {
                    return new NuspecReader(manifest);
                }

                if (Directory.EnumerateFiles(folder, "*.nupkg").FirstOrDefault() is { } nupkg)
                {
                    using var archive = new PackageArchiveReader(nupkg);
                    return new NuspecReader(archive.GetNuspec());
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or InvalidDataException or PackagingException)
        {
            // A corrupt or locked package only costs the dependency information.
        }

        return null;
    }

    /// <summary>Without an assets file, the direct packages as the project file declares them (versions from Directory.Packages.props under central management).</summary>
    private static ProjectPackages ReadUnrestored(string name, string projectPath, IReadOnlyList<string> frameworks)
    {
        XElement root;
        try
        {
            root = XDocument.Load(projectPath).Root!;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return new ProjectPackages(name, projectPath, PackageStyle.None, frameworks, [], null, false, null, []);
        }

        var references = root.Descendants().Where(e => e.Name.LocalName == "PackageReference" && e.Attribute("Include") is not null).ToList();
        if (references.Count == 0)
        {
            return new ProjectPackages(name, projectPath, PackageStyle.None, frameworks, [], null, false, null, []);
        }

        var propsFile = CentralVersionsFile(projectPath);
        var central = propsFile is not null ? CentralVersions(propsFile) : null;
        var packages = new List<InstalledPackage>();
        foreach (var reference in references)
        {
            var id = (string)reference.Attribute("Include")!;
            var declared = Value(reference, "VersionOverride") ?? Value(reference, "Version") ?? central?.GetValueOrDefault(id);
            if (declared is null || !VersionRange.TryParse(declared, out var range) || range.MinVersion is null)
            {
                continue;
            }

            packages.Add(new InstalledPackage(id, range.MinVersion, IsDirect: true, range, [], [], IsImplicit: false));
        }

        return new ProjectPackages(
            name,
            projectPath,
            PackageStyle.PackageReference,
            frameworks,
            [.. packages.DistinctBy(p => p.Identity).OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)],
            central is not null ? propsFile : projectPath,
            central is not null,
            "not restored: versions as declared, transitive packages unknown (run dotnet restore or dotnet_build)",
            []);
    }

    private static string? Value(XElement element, string name) =>
        ((string?)element.Attribute(name) ?? element.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value)?.Trim() is { Length: > 0 } value
        && !value.Contains('$', StringComparison.Ordinal) ? value : null;

    /// <summary>The Directory.Packages.props that central package management reads for this project, if it turns management on.</summary>
    internal static string? CentralVersionsFile(string projectPath)
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(projectPath)!); directory is not null; directory = directory.Parent)
        {
            var props = Path.Combine(directory.FullName, "Directory.Packages.props");
            if (!File.Exists(props))
            {
                continue;
            }

            try
            {
                var enabled = XDocument.Load(props).Descendants().LastOrDefault(e => e.Name.LocalName == "ManagePackageVersionsCentrally")?.Value.Trim();
                return string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase) ? props : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
            {
                return null;
            }
        }

        return null;
    }

    private static Dictionary<string, string> CentralVersions(string propsFile)
    {
        try
        {
            return XDocument.Load(propsFile).Descendants()
                .Where(e => e.Name.LocalName == "PackageVersion" && e.Attribute("Include") is not null && e.Attribute("Version") is not null)
                .GroupBy(e => (string)e.Attribute("Include")!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => (string)g.Last().Attribute("Version")!, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex AdvisoryUrl();
}
