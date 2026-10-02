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
}
