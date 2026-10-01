using Farol.Core;
using NuGet.Configuration;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace Farol.Engine.Packages;

/// <summary>
/// The NuGet configuration restore uses for a workspace: the nuget.config files from the workspace directory up,
/// plus the user and machine ones. It says which feeds to ask and where packages already sit on disk.
/// </summary>
public sealed class NuGetContext
{
    private NuGetContext(string directory, ISettings settings)
    {
        Directory = directory;
        Settings = settings;
        var sources = new PackageSourceProvider(settings).LoadPackageSources().Where(s => s.IsEnabled).ToList();
        Sources = sources;
        AuditSources = AuditSourcesFrom(settings) is { Count: > 0 } audit ? audit : sources;
        Mapping = PackageSourceMapping.GetPackageSourceMapping(settings);
        GlobalPackagesFolder = SettingsUtility.GetGlobalPackagesFolder(settings);
        FallbackFolders = SettingsUtility.GetFallbackPackageFolders(settings);
        RepositoryPath = SettingsUtility.GetRepositoryPath(settings);
    }

    public string Directory { get; }

    public ISettings Settings { get; }

    /// <summary>Enabled package sources, in configuration order.</summary>
    public IReadOnlyList<PackageSource> Sources { get; }

    /// <summary>Where vulnerability data comes from: the auditSources section when present, otherwise the package sources (as NuGetAudit does).</summary>
    public IReadOnlyList<PackageSource> AuditSources { get; }

    public PackageSourceMapping Mapping { get; }

    public string GlobalPackagesFolder { get; }

    public IReadOnlyList<string> FallbackFolders { get; }

    /// <summary>The packages.config packages folder when nuget.config sets repositoryPath; otherwise null (a "packages" folder next to the solution).</summary>
    public string? RepositoryPath { get; }

    public static NuGetContext Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        try
        {
            return new NuGetContext(directory, NuGet.Configuration.Settings.LoadDefaultSettings(directory));
        }
        catch (NuGetConfigurationException ex)
        {
            throw new FarolException(ErrorCodes.InvalidArgument, $"NuGet configuration could not be read: {ex.Message}", "Fix the nuget.config file it names; restore fails on it too.");
        }
    }

    /// <summary>The sources restore may use for this package: all of them, or the mapped ones when package source mapping is on.</summary>
    public IReadOnlyList<PackageSource> SourcesFor(string packageId)
    {
        if (!Mapping.IsEnabled)
        {
            return Sources;
        }

        var mapped = Mapping.GetConfiguredPackageSources(packageId);
        return [.. Sources.Where(s => mapped.Contains(s.Name, StringComparer.OrdinalIgnoreCase))];
    }

    /// <summary>
    /// The extracted package folder in the global packages folder or a fallback folder, or null. A folder counts only
    /// once extraction finished (NuGet writes the .nupkg.metadata marker last).
    /// </summary>
    public string? FindInstalled(string id, NuGetVersion version)
    {
        foreach (var root in FallbackFolders.Prepend(GlobalPackagesFolder))
        {
            var resolver = new VersionFolderPathResolver(root);
            if (File.Exists(resolver.GetNupkgMetadataPath(id, version)) || File.Exists(resolver.GetHashPath(id, version)))
            {
                return resolver.GetInstallPath(id, version);
            }
        }

        return null;
    }

    /// <summary>The packages.config folder of a package ("packages/Id.Version"), looked up from the project's directory.</summary>
    public string? FindInRepository(string projectDirectory, PackageIdentity identity)
    {
        var candidates = new[] { RepositoryPath, Path.Combine(Directory, "packages"), Path.Combine(projectDirectory, "..", "packages"), Path.Combine(projectDirectory, "packages") };
        foreach (var root in candidates.OfType<string>().Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var folder = new PackagePathResolver(root).GetInstallPath(identity);
            if (System.IO.Directory.Exists(folder))
            {
                return folder;
            }
        }

        return null;
    }

    private static List<PackageSource> AuditSourcesFrom(ISettings settings) =>
        settings.GetSection("auditSources")?.Items.OfType<AddItem>().Select(i => new PackageSource(i.Value, i.Key)).ToList() ?? [];
}
