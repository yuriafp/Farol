using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace Farol.Engine.Packages;

/// <summary>How a project declares its packages.</summary>
public enum PackageStyle
{
    None,
    PackageReference,
    PackagesConfig,
}

/// <summary>
/// One package as one project resolves it. <see cref="Via"/> is the shortest chain from a direct package (or a
/// referenced project) that brings a transitive package in; for packages.config, the installed packages that require it.
/// </summary>
public sealed record InstalledPackage(
    string Id,
    NuGetVersion Version,
    bool IsDirect,
    VersionRange? Requested,
    IReadOnlyList<string> Frameworks,
    IReadOnlyList<string> Via,
    bool IsImplicit)
{
    public PackageIdentity Identity => new(Id, Version);
}

/// <summary>A vulnerability warning (NU1901–NU1904) that the last restore wrote to the assets file.</summary>
public sealed record RestoreAuditFinding(string Id, NuGetVersion Version, string Severity, Uri? AdvisoryUrl);

/// <summary>The packages of one project file, merged across its target frameworks.</summary>
public sealed record ProjectPackages(
    string Name,
    string FilePath,
    PackageStyle Style,
    IReadOnlyList<string> Frameworks,
    IReadOnlyList<InstalledPackage> Packages,
    string? VersionsFile,
    bool CentralPackageManagement,
    string? Problem,
    IReadOnlyList<RestoreAuditFinding> RestoreAudit);

public sealed record PackageAdvisory(string Severity, Uri Url)
{
    /// <summary>"GHSA-5crp-9r3c-p9vr" for GitHub advisories, otherwise the URL.</summary>
    public string Name => Url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && Url.Segments.Length > 0
        ? Url.Segments[^1].TrimEnd('/')
        : Url.ToString();
}

public sealed record PackageDeprecation(IReadOnlyList<string> Reasons, string? AlternatePackage);

/// <summary>What the feeds say about one package version.</summary>
public sealed record PackageStatus(
    IReadOnlyList<PackageAdvisory> Vulnerabilities,
    PackageDeprecation? Deprecation,
    NuGetVersion? LatestStable,
    NuGetVersion? Latest)
{
    public static PackageStatus Unknown { get; } = new([], null, null, null);

    /// <summary>The newest listed version an upgrade would move to: stable unless only prereleases exist or the current one is a prerelease.</summary>
    public NuGetVersion? Newer(NuGetVersion current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var candidate = current.IsPrerelease || LatestStable is null ? Latest : LatestStable;
        return candidate is not null && candidate > current ? candidate : null;
    }
}

/// <summary>The outcome of asking the feeds about a set of packages. Problems name the source and what failed.</summary>
public sealed record FeedCheck(
    IReadOnlyDictionary<PackageIdentity, PackageStatus> Statuses,
    IReadOnlyList<string> Sources,
    IReadOnlyList<string> Problems,
    bool HasVulnerabilityData)
{
    public static FeedCheck None { get; } = new(new Dictionary<PackageIdentity, PackageStatus>(), [], [], HasVulnerabilityData: false);

    public PackageStatus StatusOf(PackageIdentity identity) => Statuses.GetValueOrDefault(identity) ?? PackageStatus.Unknown;
}

/// <summary>A warning or error restore recorded in a project's assets file (the message is in restore's language).</summary>
public sealed record RestoreMessage(string ProjectPath, string Code, bool IsError, string Message);
