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
}
