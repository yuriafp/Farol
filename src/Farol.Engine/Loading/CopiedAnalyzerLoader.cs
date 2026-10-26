using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;

namespace Farol.Engine.Loading;

/// <summary>
/// Loads the analyzer copies of one workspace load, and finds their dependencies among those copies only (AC-47). Roslyn's
/// own loader serves the whole process and also knows the original files, so a dependency in another folder could still
/// load from its original, and lock it, or from the copy of an earlier load. The compiler, the framework and the rest of
/// what Farol itself runs come from Farol's own context, so that analyzers and Farol share the compiler's types.
/// </summary>
internal sealed class CopiedAnalyzerLoader : IAnalyzerAssemblyLoader
{
    private readonly ConcurrentDictionary<string, List<(Version? Version, string Path)>> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FolderContext> _folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public CopiedAnalyzerLoader(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            AddDependencyLocation(path);
        }
    }

    public void AddDependencyLocation(string fullPath)
    {
        AssemblyName name;
        try
        {
            name = AssemblyName.GetAssemblyName(fullPath);
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or ArgumentException)
        {
            // Not an assembly: Roslyn reports it when it loads it.
            return;
        }

        lock (_gate)
        {
            var known = _byName.GetOrAdd(name.Name!, _ => []);
            if (!known.Exists(k => string.Equals(k.Path, fullPath, StringComparison.OrdinalIgnoreCase)))
            {
                known.Add((name.Version, fullPath));
            }
        }
    }

    public Assembly LoadFromPath(string fullPath) =>
        Folder(Path.GetDirectoryName(fullPath)!).LoadFromAssemblyName(AssemblyName.GetAssemblyName(fullPath));

    private FolderContext Folder(string directory) => _folders.GetOrAdd(directory, d => new FolderContext(this, d));

    /// <summary>A copy of <paramref name="name"/>: the one in <paramref name="directory"/>, else the newest one.</summary>
    private string? PathOf(AssemblyName name, string directory)
    {
        lock (_gate)
        {
            if (!_byName.TryGetValue(name.Name!, out var known) || known.Count == 0)
            {
                return null;
            }

            return known.FirstOrDefault(k => string.Equals(Path.GetDirectoryName(k.Path), directory, StringComparison.OrdinalIgnoreCase)).Path
                ?? known.OrderByDescending(k => k.Version).First().Path;
        }
    }

    /// <summary>One context per folder of copies, as Roslyn does: analyzers of different folders may need different versions of a library.</summary>
    private sealed class FolderContext(CopiedAnalyzerLoader loader, string directory) : AssemblyLoadContext($"Farol analyzers: {directory}")
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (FromFarol(assemblyName) is { } shared)
            {
                return shared;
            }

            if (loader.PathOf(assemblyName, directory) is not { } path)
            {
                return null;
            }

            var folder = Path.GetDirectoryName(path)!;
            return string.Equals(folder, directory, StringComparison.OrdinalIgnoreCase)
                ? LoadFromAssemblyPath(path)
                : loader.Folder(folder).LoadFromAssemblyName(assemblyName);
        }

        // The compiler, the framework and the libraries Farol runs, when Farol has the version asked for or a newer one.
        private static Assembly? FromFarol(AssemblyName name)
        {
            try
            {
                return Default.LoadFromAssemblyName(name);
            }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                return null;
            }
        }
    }
}
