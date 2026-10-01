using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NuGet.Configuration;
using NuGet.Credentials;
using NuGet.Packaging.Core;
using NuGet.Packaging.Signing;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using INuGetLogger = NuGet.Common.ILogger;

namespace Farol.Engine.Packages;

/// <summary>
/// Asks the workspace's NuGet feeds, through the NuGet client (credential providers, HTTP cache, package source
/// mapping), about vulnerabilities, deprecation and newer versions, and downloads packages into the global packages
/// folder the way restore does. With <c>--offline</c> nothing here touches the network.
/// </summary>
public sealed partial class PackageFeeds(IOptions<FarolEngineOptions> options, ILogger<PackageFeeds> logger)
{
    private const int Parallelism = 8;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);
    private static readonly Lock InitializationLock = new();
    private static bool s_initialized;

    private readonly INuGetLogger _nuget = new NuGetLogger(logger);
    private readonly ConcurrentDictionary<string, SourceRepository> _repositories = new(StringComparer.OrdinalIgnoreCase);

    public bool Offline => options.Value.Offline;

    /// <summary>Vulnerabilities, deprecation and newest versions of each package. Failures become problems, never exceptions.</summary>
    public async Task<FeedCheck> CheckAsync(NuGetContext context, IReadOnlyCollection<PackageIdentity> packages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(packages);
        if (Offline || packages.Count == 0)
        {
            return FeedCheck.None;
        }

        Initialize();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        using var cache = new SourceCacheContext();
        var problems = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var vulnerabilities = await VulnerabilityDataAsync(context, cache, problems, timeout.Token, cancellationToken);
        var metadata = new ConcurrentDictionary<string, List<IPackageSearchMetadata>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await Parallel.ForEachAsync(
                packages.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase),
                new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = timeout.Token },
                async (id, token) => metadata[id] = await MetadataAsync(context, id, cache, problems, token));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            problems.TryAdd("(timeout)", $"the feeds did not answer within {Timeout.TotalSeconds:0} s; results are partial");
        }

        var statuses = new Dictionary<PackageIdentity, PackageStatus>();
        foreach (var package in packages.Distinct())
        {
            var versions = metadata.GetValueOrDefault(package.Id) ?? [];
            var own = versions.Where(m => m.Identity.Version == package.Version).ToList();
            var deprecation = await DeprecationAsync(own);
            var advisories = (vulnerabilities?.For(package) ?? [])
                .Concat(own.SelectMany(m => m.Vulnerabilities ?? []).Where(v => v.AdvisoryUrl is not null).Select(v => new PackageAdvisory(Severity(v.Severity), v.AdvisoryUrl)))
                .DistinctBy(a => a.Url)
                .ToList();
            var listed = versions.Where(m => m.IsListed).Select(m => m.Identity.Version).ToList();
            statuses[package] = new PackageStatus(advisories, deprecation, listed.Where(v => !v.IsPrerelease).DefaultIfEmpty().Max(), listed.DefaultIfEmpty().Max());
        }

        var sources = context.Sources.Select(s => s.Name).Concat(context.AuditSources.Select(s => s.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new FeedCheck(statuses, sources, [.. problems.Select(p => p.Key.StartsWith('(') ? p.Value : $"{p.Key}: {p.Value}")], vulnerabilities is not null);
    }

    /// <summary>The newest listed version of a package on the feeds (stable unless only prereleases exist), or null.</summary>
    public async Task<(NuGetVersion? Version, IReadOnlyList<string> Problems)> LatestAsync(NuGetContext context, string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Offline)
        {
            return (null, []);
        }

        Initialize();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        using var cache = new SourceCacheContext();
        var problems = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var versions = (await MetadataAsync(context, id, cache, problems, timeout.Token)).Where(m => m.IsListed).Select(m => m.Identity.Version).ToList();
        var latest = versions.Where(v => !v.IsPrerelease).DefaultIfEmpty().Max() ?? versions.DefaultIfEmpty().Max();
        return (latest, [.. problems.Select(p => $"{p.Key}: {p.Value}")]);
    }

    /// <summary>
    /// Downloads and extracts a package into the global packages folder, as restore would, from the first source that
    /// has it. Returns the extracted folder and the source's name, or the problems met.
    /// </summary>
    public async Task<(string? Folder, string? Source, IReadOnlyList<string> Problems)> DownloadAsync(NuGetContext context, PackageIdentity identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(identity);
        if (Offline)
        {
            return (null, null, []);
        }

        Initialize();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        using var cache = new SourceCacheContext();
        var problems = new List<string>();
        var downloadContext = new PackageDownloadContext(cache) { ClientPolicyContext = ClientPolicyContext.GetClientPolicy(context.Settings, _nuget) };
        foreach (var source in context.SourcesFor(identity.Id))
        {
            try
            {
                var download = await Repository(source).GetResourceAsync<DownloadResource>(timeout.Token);
                if (download is null)
                {
                    continue;
                }

                using var result = await download.GetDownloadResourceResultAsync(identity, downloadContext, context.GlobalPackagesFolder, _nuget, timeout.Token);
                if (result.Status is DownloadResourceResultStatus.Available or DownloadResourceResultStatus.AvailableWithoutStream
                    && context.FindInstalled(identity.Id, identity.Version) is { } folder)
                {
                    LogDownloaded(logger, identity.Id, identity.Version, source.Name);
                    return (folder, source.Name, problems);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                problems.Add($"{source.Name}: no answer within {Timeout.TotalSeconds:0} s");
                break;
            }
            catch (Exception ex) when (IsFeedFailure(ex))
            {
                problems.Add($"{source.Name}: {Describe(ex)}");
            }
        }

        return (null, null, problems);
    }

    private async Task<VulnerabilityData?> VulnerabilityDataAsync(
        NuGetContext context, SourceCacheContext cache, ConcurrentDictionary<string, string> problems, CancellationToken token, CancellationToken callerToken)
    {
        var files = new ConcurrentBag<IReadOnlyDictionary<string, IReadOnlyList<NuGet.Protocol.Model.PackageVulnerabilityInfo>>>();
        var provided = 0;
        await Task.WhenAll(context.AuditSources.Select(async source =>
        {
            try
            {
                var resource = await Repository(source).GetResourceAsync<IVulnerabilityInfoResource>(token);
                if (resource is null)
                {
                    return;
                }

                var result = await resource.GetVulnerabilityInfoAsync(cache, _nuget, token);
                if (result.Exceptions is { } failure)
                {
                    problems.TryAdd(source.Name, Describe(failure));
                }

                foreach (var file in result.KnownVulnerabilities ?? [])
                {
                    files.Add(file);
                }

                Interlocked.Increment(ref provided);
            }
            catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
            {
                problems.TryAdd(source.Name, $"no vulnerability data within {Timeout.TotalSeconds:0} s");
            }
            catch (Exception ex) when (IsFeedFailure(ex))
            {
                problems.TryAdd(source.Name, Describe(ex));
            }
        }));

        return provided > 0 ? new VulnerabilityData([.. files]) : null;
    }

    private async Task<List<IPackageSearchMetadata>> MetadataAsync(
        NuGetContext context, string id, SourceCacheContext cache, ConcurrentDictionary<string, string> problems, CancellationToken token)
    {
        var all = new List<IPackageSearchMetadata>();
        foreach (var source in context.SourcesFor(id))
        {
            try
            {
                var resource = await Repository(source).GetResourceAsync<PackageMetadataResource>(token);
                if (resource is not null)
                {
                    all.AddRange(await resource.GetMetadataAsync(id, includePrerelease: true, includeUnlisted: true, cache, _nuget, token));
                }
            }
            catch (Exception ex) when (IsFeedFailure(ex) && ex is not OperationCanceledException)
            {
                problems.TryAdd(source.Name, Describe(ex));
            }
        }

        return all;
    }

    private static async Task<PackageDeprecation?> DeprecationAsync(IEnumerable<IPackageSearchMetadata> versions)
    {
        foreach (var version in versions)
        {
            if (await version.GetDeprecationMetadataAsync() is { } deprecation)
            {
                var reasons = deprecation.Reasons?.Select(Reason).ToList() ?? [];
                return new PackageDeprecation(reasons.Count > 0 ? reasons : ["other"], deprecation.AlternatePackage?.PackageId);
            }
        }

        return null;
    }

    private static string Reason(string reason) => reason switch
    {
        "Legacy" => "legacy",
        "CriticalBugs" => "critical bugs",
        _ => reason.ToLowerInvariant(),
    };

    private static string Severity(int severity) => severity switch
    {
        0 => "low",
        1 => "moderate",
        2 => "high",
        3 => "critical",
        _ => "unknown severity",
    };

    private SourceRepository Repository(PackageSource source) =>
        _repositories.GetOrAdd(source.Source, _ => NuGet.Protocol.Core.Types.Repository.Factory.GetCoreV3(source));

    private static bool IsFeedFailure(Exception ex) =>
        ex is FatalProtocolException or HttpRequestException or IOException or InvalidOperationException or AggregateException or OperationCanceledException or UnauthorizedAccessException
        || ex.GetType().Namespace?.StartsWith("NuGet", StringComparison.Ordinal) == true;

    /// <summary>NuGet wraps the useful part (401, DNS failure…) in inner exceptions.</summary>
    private static string Describe(Exception ex)
    {
        var messages = new List<string>();
        for (var current = ex; current is not null; current = current is AggregateException aggregate ? aggregate.InnerExceptions.FirstOrDefault() : current.InnerException)
        {
            var message = current.Message.Trim();
            if (message.Length > 0 && !messages.Any(m => m.Contains(message, StringComparison.Ordinal)))
            {
                messages.Add(message);
            }
        }

        var text = string.Join(" ← ", messages);
        if (text.Contains("401", StringComparison.Ordinal) || text.Contains("403", StringComparison.Ordinal))
        {
            text += " (credentials: run 'dotnet restore --interactive' once so the credential provider caches a token)";
        }

        return text.Length > 300 ? text[..300] + "…" : text;
    }

    /// <summary>Credential providers (Azure Artifacts and others, never interactive) and the user agent: process-wide in the NuGet client.</summary>
    private void Initialize()
    {
        lock (InitializationLock)
        {
            if (s_initialized)
            {
                return;
            }

            UserAgent.SetUserAgentString(new UserAgentStringBuilder("Farol.Mcp"));
            DefaultCredentialServiceUtility.SetupDefaultCredentialService(_nuget, nonInteractive: true);
            s_initialized = true;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Downloaded {Id} {Version} from {Source} into the global packages folder")]
    private static partial void LogDownloaded(ILogger logger, string id, NuGetVersion version, string source);

    /// <summary>Every vulnerability file of every audit source, keyed by lower-case package id.</summary>
    private sealed class VulnerabilityData(IReadOnlyList<IReadOnlyDictionary<string, IReadOnlyList<NuGet.Protocol.Model.PackageVulnerabilityInfo>>> files)
    {
        public IEnumerable<PackageAdvisory> For(PackageIdentity package) =>
            files.SelectMany(f => f.TryGetValue(package.Id.ToLowerInvariant(), out var entries) ? entries : [])
                .Where(v => v.Versions.Satisfies(package.Version))
                .Select(v => new PackageAdvisory(Severity((int)v.Severity), v.Url));
    }

    /// <summary>NuGet's own logging, sent to Farol's (stderr) at debug level: restore-like chatter is not news for agents.</summary>
    private sealed class NuGetLogger(ILogger logger) : NuGet.Common.LoggerBase
    {
        public override void Log(NuGet.Common.ILogMessage message)
        {
            var level = message.Level >= NuGet.Common.LogLevel.Warning ? LogLevel.Debug : LogLevel.Trace;
            if (logger.IsEnabled(level))
            {
#pragma warning disable CA1848, CA2254 // NuGet messages are free text.
                logger.Log(level, "NuGet: {Message}", message.Message);
#pragma warning restore CA1848, CA2254
            }
        }

        public override Task LogAsync(NuGet.Common.ILogMessage message)
        {
            Log(message);
            return Task.CompletedTask;
        }
    }
}
