namespace Farol.Engine.Loading;

/// <summary>
/// Paths written on Windows (project files, HintPaths) name files in any case; Linux file systems are case-sensitive.
/// Finds the file or directory whose path matches, segment by segment, ignoring case. On Windows a path is what it is.
/// </summary>
internal static class PathCase
{
    /// <summary>The existing file at <paramref name="path"/> in any case, or null.</summary>
    public static string? File(string path) => Resolve(path, file: true);

    /// <summary>The existing directory at <paramref name="path"/> in any case, or null.</summary>
    public static string? Directory(string path) => Resolve(path, file: false);

    private static string? Resolve(string path, bool file)
    {
        if (file ? System.IO.File.Exists(path) : System.IO.Directory.Exists(path))
        {
            return path;
        }

        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "/";
        var current = root;
        var segments = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            var candidate = Path.Combine(current, segments[i]);
            var last = i == segments.Length - 1;
            if (last ? (file ? System.IO.File.Exists(candidate) : System.IO.Directory.Exists(candidate)) : System.IO.Directory.Exists(candidate))
            {
                current = candidate;
                continue;
            }

            var wantFile = last && file;
            var match = Children(current).FirstOrDefault(e => string.Equals(Path.GetFileName(e), segments[i], StringComparison.OrdinalIgnoreCase)
                && (wantFile ? System.IO.File.Exists(e) : System.IO.Directory.Exists(e)));
            if (match is null)
            {
                return null;
            }

            current = match;
        }

        return file ? (System.IO.File.Exists(current) ? current : null) : (System.IO.Directory.Exists(current) ? current : null);
    }

    // Not cached: files appear (a package downloaded, a file added) between loads of the same process.
    private static string[] Children(string directory)
    {
        try
        {
            return System.IO.Directory.Exists(directory) ? [.. System.IO.Directory.EnumerateFileSystemEntries(directory)] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
