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
        ["--read-only"] = $"{Section}:ReadOnly",
        ["--offline"] = $"{Section}:Offline",
        ["--usage-log"] = $"{Section}:UsageLog",
    };

    // Switches that may be passed bare: "--read-only" means "--read-only true".
    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase) { "--read-only", "--autoload", "--offline", "--usage-log" };

    public static string Version { get; } =
        typeof(HostConfiguration).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    /// <summary>
    /// Gives bare flags an explicit value. The configuration parser would otherwise take the next argument as the
    /// flag's value, so "--read-only --workspace App.sln" would swallow "--workspace".
    /// </summary>
    public static string[] ExpandFlags(string[] args)
    {
        var expanded = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            var hasValue = i + 1 < args.Length && bool.TryParse(args[i + 1], out _);
            expanded.Add(Flags.Contains(args[i]) && !hasValue ? $"{args[i]}=true" : args[i]);
        }

        return [.. expanded];
    }
}
