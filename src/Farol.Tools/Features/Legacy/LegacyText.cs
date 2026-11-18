using Farol.Core.Paths;
using Farol.Engine.Legacy;

namespace Farol.Tools.Features.Legacy;

/// <summary>Formatting shared by the legacy tools: compact place lists and a stable order of technology areas.</summary>
internal static class LegacyText
{
    public static int AreaOrder(string area) => LegacyCatalog.AreaOrder(area);

    public static string Places(IEnumerable<string> locations, string root, int max = 6) => DisplayPath.Places(root, locations, max);

    public static string Places(IEnumerable<UsageLocation> locations, string root, int max = 6) =>
        DisplayPath.Places(root, locations.Select(l => $"{l.FilePath}:{l.Line}"), max);

    /// <summary>The first places as display paths, for structured output; the full count goes alongside.</summary>
    public static IReadOnlyList<string> Located(IEnumerable<string> locations, string root, int max = 20) =>
        [.. locations.Take(max).Select(l => DisplayPath.InText(root, l))];

    public static IReadOnlyList<string> Located(IEnumerable<UsageLocation> locations, string root, int max = 20) =>
        [.. locations.Take(max).Select(l => DisplayPath.Location(root, l.FilePath, l.Line))];
}
