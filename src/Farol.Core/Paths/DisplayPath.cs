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
    /// "Folder/File.cs:14, :26, Other.cs:3, … 4 more": files and "file:line" locations made root-relative, a repeated
    /// file folded into ":line", at most <paramref name="max"/> entries.
    /// </summary>
    public static string Places(string root, IEnumerable<string> locations, int max = 6)
    {
        ArgumentNullException.ThrowIfNull(locations);
        var list = locations.Select(location =>
        {
            var colon = location.LastIndexOf(':');
            return colon > 2 && int.TryParse(location.AsSpan(colon + 1), out var line)
                ? (File: From(root, location[..colon]), Line: (int?)line)
                : (File: From(root, location), Line: null);
        }).ToList();

        var written = new List<string>();
        string? previous = null;
        foreach (var (file, line) in list.Take(max))
        {
            written.Add(line is null ? file : file == previous ? $":{line}" : $"{file}:{line}");
            previous = file;
        }

        return list.Count > max ? $"{string.Join(", ", written)}, … {list.Count - max} more" : string.Join(", ", written);
    }

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
