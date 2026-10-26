namespace Farol.Core.Paths;

/// <summary>
/// The directories Farol may load, read and write: the root it was started in plus <c>Farol:TrustedPaths</c>.
/// Loading a solution runs its build logic and code actions write files, so every path a tool touches must
/// resolve inside one of them after <c>..</c> segments and symbolic links (junctions included) are resolved.
/// </summary>
public sealed class PathSandbox
{
    private const int MaxLinkDepth = 32;

    private static readonly StringComparison Comparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    private readonly string[] _trusted;

    public PathSandbox(string rootDirectory, IEnumerable<string>? trustedPaths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        Root = Path.GetFullPath(rootDirectory);
        TrustedPaths = [.. (trustedPaths ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Path.GetFullPath(p.Trim(), Root))];
        _trusted = [.. new[] { Root }.Concat(TrustedPaths).Select(Canonical)];
    }

    public string Root { get; }

    /// <summary>Extra directories from configuration, as full paths.</summary>
    public IReadOnlyList<string> TrustedPaths { get; }

    /// <summary>
    /// Resolves a tool's path argument (absolute, or relative to the root) and refuses it outside the trusted directories.
    /// A relative path that names nothing under the root but a file or folder under <paramref name="fallbackDirectory"/>
    /// (the workspace's folder, when the root is above it) resolves there: agents often write paths from the solution's folder.
    /// </summary>
    public string Resolve(string path, string? fallbackDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var given = path.Trim();
        var fullPath = FullPath(given, Root);
        if (fallbackDirectory is not null && !Path.IsPathRooted(given) && !Path.Exists(fullPath))
        {
            var alternative = FullPath(given, fallbackDirectory);
            if (Contains(alternative) && Path.Exists(alternative))
            {
                fullPath = alternative;
            }
        }

        Demand(fullPath, $"Path '{given}'");
        return fullPath;
    }

    /// <summary>
    /// The full path of a path a caller supplied. One the file system cannot take (a null character, too long) is an
    /// <c>invalid_argument</c> error that quotes only its start: such an argument can be megabytes long, or binary.
    /// </summary>
    public static string FullPath(string path, string basePath, string subject = "Path")
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            return Path.GetFullPath(path, basePath);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            throw new FarolException(
                ErrorCodes.InvalidArgument,
                path.Contains('\0', StringComparison.Ordinal)
                    ? $"{subject} '{Echo(path)}' contains a null character."
                    : $"{subject} '{Echo(path)}' ({path.Length} characters) is not a path the file system accepts.",
                "Pass a file or folder path, relative to the root as responses write paths, or absolute.");
        }
    }

    private static string Echo(string given)
    {
        // Never between the two halves of a surrogate pair: the response could not be encoded.
        var cut = given.Length <= 60 ? given.Length : char.IsHighSurrogate(given[59]) ? 59 : 60;
        return (cut < given.Length ? given[..cut] + "…" : given).Replace("\0", "\\0", StringComparison.Ordinal);
    }

    public bool Contains(string fullPath)
    {
        var canonical = Canonical(fullPath);
        return _trusted.Any(directory => IsSameOrUnder(canonical, directory));
    }

    /// <summary>Throws an actionable error when <paramref name="fullPath"/> is outside the trusted directories.</summary>
    public void Demand(string fullPath, string subject)
    {
        if (Contains(fullPath))
        {
            return;
        }

        var trusted = TrustedPaths.Count == 0 ? $"the root {Display(Root)}" : $"the root {Display(Root)} and Farol:TrustedPaths";
        throw new FarolException(
            ErrorCodes.PathNotTrusted,
            $"{subject} resolves outside the directories Farol trusts ({trusted}): {Display(Canonical(fullPath))}.",
            "Loading a solution runs its build logic, so Farol only reads, loads and writes inside them. If the user wants it, " +
            "ask them to add the directory to Farol:TrustedPaths or to start Farol with --root pointing above it.");
    }

    /// <summary>The path with <c>..</c> collapsed and every existing symbolic link or junction replaced by its target.</summary>
    public static string Canonical(string path) => ResolveLinks(Path.GetFullPath(path), depth: 0);

    private static string ResolveLinks(string fullPath, int depth)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            return fullPath;
        }

        var parts = fullPath[root.Length..].Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < parts.Length; i++)
        {
            var next = Path.Combine(current, parts[i]);
            var target = LinkTarget(next);
            if (target is null)
            {
                if (!Path.Exists(next))
                {
                    // Nothing below a missing entry can be a link: the rest stays as written.
                    return Path.Combine([next, .. parts[(i + 1)..]]);
                }
            }
            else
            {
                if (depth >= MaxLinkDepth)
                {
                    throw new IOException($"Too many levels of symbolic links in '{fullPath}'.");
                }

                next = ResolveLinks(target, depth + 1);
            }

            current = next;
        }

        return current;
    }

    private static string? LinkTarget(string path)
    {
        try
        {
            FileSystemInfo entry = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (entry.LinkTarget is null)
            {
                return null;
            }

            return entry.ResolveLinkTarget(returnFinalTarget: true) is { } final ? Path.GetFullPath(final.FullName) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="directory"/> or below it, also when the directory is a drive root such as <c>D:\</c>.</summary>
    public static bool IsSameOrUnder(string path, string directory)
    {
        var trimmed = directory.TrimEnd(Separators);
        return path.TrimEnd(Separators).Equals(trimmed, Comparison)
            || path.StartsWith(trimmed + Path.DirectorySeparatorChar, Comparison);
    }

    private static string Display(string path) => path.Replace('\\', '/');
}
