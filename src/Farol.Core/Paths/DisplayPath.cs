using System.Text.RegularExpressions;

namespace Farol.Core.Paths;

/// <summary>Formats paths the way agents read them best: relative to the workspace root, with '/'.</summary>
public static class DisplayPath
{
    public static string From(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        var outsideRoot = relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative);
        return (outsideRoot ? fullPath : relative).Replace('\\', '/');
    }

    public static string Location(string root, string fullPath, int line) => $"{From(root, fullPath)}:{line}";

    /// <summary>
    /// Rewrites the absolute paths under <paramref name="root"/> that a message from MSBuild or Roslyn mentions as
    /// root-relative paths with '/'. A path ends at whitespace or a quote.
    /// </summary>
    public static string InText(string root, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(text);
        var segments = root.TrimEnd('\\', '/').Split('\\', '/').Select(Regex.Escape);
        var pattern = string.Join(@"[\\/]", segments) + @"[\\/](?<rest>[^\s'""<>|*?]+)";
        return Regex.Replace(text, pattern, m => m.Groups["rest"].Value.Replace('\\', '/'), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }
}
