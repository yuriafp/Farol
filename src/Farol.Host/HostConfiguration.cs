using System.Reflection;

namespace Farol.Host;

internal static class HostConfiguration
{
    public const string Section = "Farol";

    /// <summary>Short command-line switches mapped onto the "Farol" configuration section.</summary>
    public static IDictionary<string, string> SwitchMappings { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["--workspace"] = $"{Section}:DefaultWorkspace",
        ["--root"] = $"{Section}:RootDirectory",
        ["--autoload"] = $"{Section}:AutoLoad",
    };

    public static string Version { get; } =
        typeof(HostConfiguration).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";
}
