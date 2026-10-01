namespace Farol.Engine;

public sealed class FarolEngineOptions
{
    /// <summary>Directory used to discover the default workspace. Defaults to the process working directory. Trusted.</summary>
    public string RootDirectory { get; set; } = Environment.CurrentDirectory;

    /// <summary>
    /// Explicit default workspace: a .sln, .slnx, .slnf, .csproj or .vbproj file, or a directory.
    /// When null, it is discovered under <see cref="RootDirectory"/>.
    /// </summary>
    public string? DefaultWorkspace { get; set; }

    /// <summary>Start loading the default workspace in the background at startup.</summary>
    public bool AutoLoad { get; set; } = true;

    /// <summary>
    /// Directories besides <see cref="RootDirectory"/> whose solutions may be loaded and whose files may be read or
    /// written. Loading a solution runs its build logic, so anything else is refused.
    /// </summary>
    public IList<string> TrustedPaths { get; } = [];

    /// <summary>Refuse writing files, building and running tests (<c>--read-only</c>).</summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    /// Never touch the network (<c>--offline</c>): package checks use only what restore left on disk, and package APIs only
    /// the local NuGet caches.
    /// </summary>
    public bool Offline { get; set; }

    /// <summary>A build that runs longer is stopped, with its whole process tree.</summary>
    public int BuildTimeoutMinutes { get; set; } = 15;

    /// <summary>A test run (per test project) that runs longer is stopped, with its whole process tree.</summary>
    public int TestTimeoutMinutes { get; set; } = 20;

    /// <summary>Global MSBuild properties for design-time builds, e.g. Configuration=Release.</summary>
    public IDictionary<string, string> MSBuildProperties { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
