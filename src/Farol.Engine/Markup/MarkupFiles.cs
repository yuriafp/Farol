namespace Farol.Engine.Markup;

/// <summary>Markup that references code without the compiler seeing it: WebForms, ASP.NET handlers, WCF and XAML.</summary>
public static class MarkupFiles
{
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".aspx", ".ascx", ".master", ".asax", ".ashx", ".asmx", ".svc", ".xaml",
    };

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".idea", "node_modules", "packages",
    };

    public static bool IsXaml(string path) => string.Equals(Path.GetExtension(path), ".xaml", StringComparison.OrdinalIgnoreCase);

    internal static IEnumerable<string> Enumerate(string directory)
    {
        var pending = new Stack<string>([directory]);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> files, children;
            try
            {
                files = Directory.EnumerateFiles(current).Where(f => Extensions.Contains(Path.GetExtension(f))).ToList();
                children = Directory.EnumerateDirectories(current).Where(d => !SkippedDirectories.Contains(Path.GetFileName(d))).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            foreach (var child in children)
            {
                pending.Push(child);
            }
        }
    }
}
