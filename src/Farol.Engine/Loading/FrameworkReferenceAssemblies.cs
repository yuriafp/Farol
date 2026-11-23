using Farol.Engine.Packages;
using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace Farol.Engine.Loading;

/// <summary>
/// The .NET Framework reference assemblies a heuristic load compiles against, from the
/// Microsoft.NETFramework.ReferenceAssemblies.netXY packages (the ones the .NET SDK uses to build .NET Framework
/// projects without targeting packs), and the packages.config packages a classic project's HintPaths point at.
/// Both are taken from the NuGet global packages folder, and downloaded into it when missing unless Farol is offline.
/// </summary>
internal sealed class FrameworkReferenceAssemblies(PackageFeeds feeds, NuGetContext nuget)
{
    private static readonly NuGetVersion ReferencePackageVersion = NuGetVersion.Parse("1.0.3");

    private readonly Dictionary<string, Task<IReadOnlyList<string>>> _frameworks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<PackageIdentity, Task<string?>> _packages = [];
    private readonly Lock _gate = new();

    /// <summary>What could not be found or downloaded, worded for the load's issues.</summary>
    public List<string> Missing { get; } = [];

    /// <summary>
    /// "v4.7.2" → the folders of its reference assemblies (…/build/.NETFramework/v4.7.2), the framework's own first and
    /// then those it builds on (v3.5 includes v3.0 and v2.0, as its RedistList says); empty with the reason in <see cref="Missing"/>.
    /// </summary>
    public Task<IReadOnlyList<string>> FrameworkAsync(string version, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_frameworks.TryGetValue(version, out var task))
            {
                _frameworks[version] = task = FindFrameworkAsync(version, cancellationToken);
            }

            return task;
        }
    }

    /// <summary>The folder of a packages.config package in the global packages folder, or null with the reason in <see cref="Missing"/>.</summary>
    public Task<string?> PackageAsync(ConfigPackage package, string source, CancellationToken cancellationToken)
    {
        if (!NuGetVersion.TryParse(package.Version, out var version))
        {
            Record($"package {package.Id} {package.Version} ({source}): not a version the heuristic load can resolve");
            return Task.FromResult<string?>(null);
        }

        var identity = new PackageIdentity(package.Id, version);
        lock (_gate)
        {
            if (!_packages.TryGetValue(identity, out var task))
            {
                _packages[identity] = task = FindPackageAsync(identity, $"package {package.Id} {package.Version} ({source})", cancellationToken);
            }

            return task;
        }
    }

    private async Task<IReadOnlyList<string>> FindFrameworkAsync(string version, CancellationToken cancellationToken)
    {
        var trimmed = version.TrimStart('v', 'V');
        var id = $"Microsoft.NETFramework.ReferenceAssemblies.net{trimmed.Replace(".", string.Empty, StringComparison.Ordinal)}";
        var folder = await FindPackageAsync(new PackageIdentity(id, ReferencePackageVersion), $"the .NET Framework {trimmed} reference assemblies (package {id} {ReferencePackageVersion})", cancellationToken);
        if (folder is null)
        {
            return [];
        }

        var root = PathCase.Directory(Path.Combine(folder, "build", ".NETFramework"));
        var folders = new List<string>();
        for (var current = $"v{trimmed}"; current is not null && root is not null && PathCase.Directory(Path.Combine(root, current)) is { } assemblies && !folders.Contains(assemblies);)
        {
            folders.Add(assemblies);
            current = IncludedFramework(assemblies);
        }

        if (folders.Count == 0)
        {
            Record($"the .NET Framework {trimmed} reference assemblies: {id} has no build/.NETFramework/v{trimmed} folder");
        }

        return folders;
    }

    // RedistList/FrameworkList.xml of v3.5: <FileList … IncludeFramework="v3.0">; the folder it builds on, or null.
    private static string? IncludedFramework(string assemblies)
    {
        if (PathCase.File(Path.Combine(assemblies, "RedistList", "FrameworkList.xml")) is not { } list)
        {
            return null;
        }

        try
        {
            return (string?)System.Xml.Linq.XDocument.Load(list).Root?.Attribute("IncludeFramework") is { Length: > 0 } included ? included : null;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException)
        {
            return null;
        }
    }

    private async Task<string?> FindPackageAsync(PackageIdentity identity, string what, CancellationToken cancellationToken)
    {
        if (nuget.FindInstalled(identity.Id, identity.Version) is { } installed)
        {
            return installed;
        }

        if (feeds.Offline)
        {
            Record($"{what}: not in the NuGet cache ({nuget.GlobalPackagesFolder}), and Farol runs offline");
            return null;
        }

        var (folder, _, problems) = await feeds.DownloadAsync(nuget, identity, cancellationToken);
        if (folder is null)
        {
            Record($"{what}: not in the NuGet cache, and the download failed{(problems.Count > 0 ? $" ({string.Join("; ", problems)})" : string.Empty)}");
        }

        return folder;
    }

    private void Record(string message)
    {
        lock (_gate)
        {
            Missing.Add(message);
        }
    }
}
