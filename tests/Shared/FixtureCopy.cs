namespace Farol.Testing;

/// <summary>
/// A disposable copy of a fixture under the temp directory, for tests that edit files, rebuild, or
/// run in another test process. The fixtures under tests/fixtures are never mutated.
/// </summary>
public sealed class FixtureCopy : IDisposable
{
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj" };

    private FixtureCopy(string root)
    {
        Root = root;
    }

    public string Root { get; }

    public static FixtureCopy Create(string fixtureDirectory)
    {
        var root = Directory.CreateTempSubdirectory("farol-fixture-").FullName;
        Copy(new DirectoryInfo(fixtureDirectory), root);
        return new FixtureCopy(root);
    }

    public string PathOf(params string[] parts) => Path.Combine([Root, .. parts]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // MSBuild build hosts can hold files for a moment after a load; temp cleanup is best effort.
        }
    }

    private static void Copy(DirectoryInfo source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in source.EnumerateFiles())
        {
            file.CopyTo(Path.Combine(target, file.Name));
        }

        foreach (var child in source.EnumerateDirectories().Where(d => !Skipped.Contains(d.Name)))
        {
            Copy(child, Path.Combine(target, child.Name));
        }
    }
}
