using System.Xml;
using System.Xml.Linq;

namespace Farol.Engine.Workspaces;

/// <summary>Facts read straight from a project file, without MSBuild evaluation.</summary>
public sealed record ProjectFileFacts(
    string Path,
    string Language,
    bool IsSdkStyle,
    string? Sdk,
    IReadOnlyList<string> DeclaredTargetFrameworks,
    string? OutputType,
    IReadOnlyList<string> ProjectTypeGuids,
    bool HasPackagesConfig);

/// <summary>Well-known classic project flavors (the ProjectTypeGuids element of legacy projects).</summary>
public static class ProjectTypeGuids
{
    public const string WebApplication = "349C5851-65DF-11DA-9384-00065B846F21";
    public const string Wpf = "60DC8134-EBA5-43B8-BCC9-BB4BC16C2548";
    public const string Wcf = "3D9AD99F-2412-4246-B90B-4EAA41C64699";
    public const string Test = "3AC096D0-A1C2-E12C-1390-A8335801FDAB";
}

public static class ProjectFileInspector
{
    public static ProjectFileFacts Inspect(string projectPath)
    {
        var language = Path.GetExtension(projectPath).ToUpperInvariant() switch
        {
            ".VBPROJ" => "VB",
            ".FSPROJ" => "F#",
            _ => "C#",
        };
        var hasPackagesConfig = File.Exists(Path.Combine(Path.GetDirectoryName(projectPath)!, "packages.config"));

        XElement root;
        try
        {
            root = XDocument.Load(projectPath).Root!;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return new ProjectFileFacts(projectPath, language, false, null, [], null, [], hasPackagesConfig);
        }

        var sdk = (string?)root.Attribute("Sdk")
            ?? Elements(root, "Sdk").Select(e => (string?)e.Attribute("Name")).FirstOrDefault(n => n is not null)
            ?? Elements(root, "Import").Select(e => (string?)e.Attribute("Sdk")).FirstOrDefault(n => n is not null);

        var frameworks = Values(root, "TargetFrameworks")
            .Concat(Values(root, "TargetFramework"))
            .SelectMany(v => v.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Concat(Values(root, "TargetFrameworkVersion").Select(ToShortFrameworkName))
            .Where(tfm => !tfm.Contains('$', StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var typeGuids = Values(root, "ProjectTypeGuids")
            .SelectMany(v => v.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(g => g.Trim('{', '}').ToUpperInvariant())
            .ToList();

        return new ProjectFileFacts(
            projectPath,
            language,
            IsSdkStyle: sdk is not null,
            sdk,
            frameworks,
            Values(root, "OutputType").FirstOrDefault(),
            typeGuids,
            hasPackagesConfig);
    }

    /// <summary>"v4.7.2" → "net472", "v3.5" → "net35".</summary>
    public static string ToShortFrameworkName(string version) =>
        "net" + version.TrimStart('v', 'V').Replace(".", string.Empty, StringComparison.Ordinal);

    // Classic projects use the MSBuild 2003 XML namespace and SDK-style ones usually none: match local names.
    private static IEnumerable<XElement> Elements(XElement root, string localName) =>
        root.Descendants().Where(e => e.Name.LocalName == localName);

    private static IEnumerable<string> Values(XElement root, string localName) =>
        Elements(root, localName).Select(e => e.Value.Trim()).Where(v => v.Length > 0);
}
