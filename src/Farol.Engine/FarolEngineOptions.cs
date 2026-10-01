namespace Farol.Engine;

public sealed class FarolEngineOptions
{
    /// <summary>Directory used to discover the default workspace. Defaults to the process working directory.</summary>
    public string RootDirectory { get; set; } = Environment.CurrentDirectory;

    /// <summary>
    /// Explicit default workspace: a .sln, .slnx, .slnf, .csproj or .vbproj file, or a directory.
    /// When null, it is discovered under <see cref="RootDirectory"/>.
    /// </summary>
    public string? DefaultWorkspace { get; set; }

    /// <summary>Start loading the default workspace in the background at startup.</summary>
    public bool AutoLoad { get; set; } = true;

    /// <summary>Global MSBuild properties for design-time builds, e.g. Configuration=Release.</summary>
    public IDictionary<string, string> MSBuildProperties { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
