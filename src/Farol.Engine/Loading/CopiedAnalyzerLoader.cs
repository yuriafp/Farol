using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Security;
using Microsoft.CodeAnalysis;

namespace Farol.Engine.Loading;

/// <summary>
/// Loads the analyzer copies of a workspace load, and finds their dependencies among those copies only (AC-47). Roslyn's
/// own loader serves the whole process and also knows the original files, so a dependency in another folder could still
/// load from its original, and lock it, or from the copy of an earlier load. The compiler, the framework and the rest of
/// what Farol itself runs come from Farol's own context, so that analyzers and Farol share the compiler's types.
/// </summary>
internal sealed class CopiedAnalyzerLoader : IAnalyzerAssemblyLoader
{
    // A folder of copies is named after its files, so one that needs nothing from another folder loads the same way in
    // every load: the loads share its context, and a reload after a rebuild loads again only the folders that depend on
    // what changed. Contexts cannot be unloaded, so loading every folder again would grow the server on each reload.
    private static readonly ConcurrentDictionary<string, Lazy<FolderContext?>> SelfContained = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, List<(Version? Version, string Path)>> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _unread = [];
    private readonly ConcurrentDictionary<string, Lazy<FolderContext>> _folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlySet<string> _copyFolders;
    private readonly Lock _gate = new();

    /// <param name="paths">The analyzers of the load: copies, and the original files of the folders that could not be copied.</param>
    /// <param name="copyFolders">The folders of <paramref name="paths"/> that hold copies, whose files never change.</param>
    public CopiedAnalyzerLoader(IEnumerable<string> paths, IReadOnlySet<string> copyFolders)
    {
        _copyFolders = copyFolders;
        foreach (var path in paths)
        {
            AddDependencyLocation(path);
        }
    }

    public void AddDependencyLocation(string fullPath)
    {
        lock (_gate)
        {
            if (!Register(fullPath))
            {
                _unread.Add(fullPath);
            }
        }
    }

    // Throws while the file cannot be read, and Roslyn then reports the analyzer as not loaded.
    public Assembly LoadFromPath(string fullPath) =>
        Folder(Path.GetDirectoryName(fullPath)!).LoadFromAssemblyName(AssemblyName.GetAssemblyName(fullPath));

    private FolderContext Folder(string directory)
    {
        if (_copyFolders.Contains(directory)
            && SelfContained.GetOrAdd(directory, d => new(() => SharedContext(d))).Value is { } shared)
        {
            return shared;
        }

        return _folders.GetOrAdd(directory, d => new(() => new FolderContext(d, this, own: null))).Value;
    }

    /// <summary>A copy of <paramref name="name"/>: the one in <paramref name="directory"/>, else the newest one.</summary>
    private string? PathOf(AssemblyName name, string directory)
    {
        lock (_gate)
        {
            if (!_byName.ContainsKey(name.Name!))
            {
                // An original a build was writing when the load began may be readable now.
                _unread.RemoveAll(Register);
            }

            if (!_byName.TryGetValue(name.Name!, out var known))
            {
                return null;
            }

            return known.FirstOrDefault(k => SameFolder(k.Path, directory)).Path ?? known.MaxBy(k => k.Version).Path;
        }
    }

    // False while the file cannot be read, such as while a build writes it.
    private bool Register(string fullPath)
    {
        AssemblyName name;
        try
        {
            name = AssemblyName.GetAssemblyName(fullPath);
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or ArgumentException or UnauthorizedAccessException or SecurityException or NotSupportedException)
        {
            return false;
        }

        var known = _byName.TryGetValue(name.Name!, out var list) ? list : _byName[name.Name!] = [];
        if (!known.Exists(k => string.Equals(k.Path, fullPath, StringComparison.OrdinalIgnoreCase)))
        {
            known.Add((name.Version, fullPath));
        }

        return true;
    }

    /// <summary>The context all loads share for a folder of copies, when every assembly its analyzers reference is in the folder or comes from Farol.</summary>
    private FolderContext? SharedContext(string directory)
    {
        Dictionary<string, string> own;
        lock (_gate)
        {
            if (_unread.Exists(path => SameFolder(path, directory)))
            {
                return null;
            }

            own = _byName
                .SelectMany(n => n.Value.Where(k => SameFolder(k.Path, directory)).Select(k => (n.Key, k.Path)))
                .DistinctBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(k => k.Key, k => k.Path, StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            foreach (var path in own.Values)
            {
                using var stream = File.OpenRead(path);
                using var image = new PEReader(stream);
                var metadata = image.GetMetadataReader();
                foreach (var handle in metadata.AssemblyReferences)
                {
                    var reference = metadata.GetAssemblyReference(handle).GetAssemblyName();
                    if (!own.ContainsKey(reference.Name!) && FromFarol(reference) is null)
                    {
                        return null;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            return null;
        }

        return new FolderContext(directory, loader: null, own);
    }

    // The compiler, the framework and the libraries Farol runs, when Farol has the version asked for or a newer one.
    private static Assembly? FromFarol(AssemblyName name)
    {
        try
        {
            return AssemblyLoadContext.Default.LoadFromAssemblyName(name);
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return null;
        }
    }

    private static bool SameFolder(string path, string directory) =>
        string.Equals(Path.GetDirectoryName(path), directory, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One context per folder, as Roslyn does: analyzers of different folders may need different versions of a library. A
    /// shared context knows its own files only; the others ask their loader, which also knows the load's other folders.
    /// </summary>
    private sealed class FolderContext(string directory, CopiedAnalyzerLoader? loader, Dictionary<string, string>? own)
        : AssemblyLoadContext($"Farol analyzers: {directory}")
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (FromFarol(assemblyName) is { } shared)
            {
                return shared;
            }

            if (own is not null)
            {
                return own.TryGetValue(assemblyName.Name!, out var file) ? LoadFromAssemblyPath(file) : null;
            }

            if (loader!.PathOf(assemblyName, directory) is not { } path)
            {
                return null;
            }

            var folder = Path.GetDirectoryName(path)!;
            return string.Equals(folder, directory, StringComparison.OrdinalIgnoreCase)
                ? LoadFromAssemblyPath(path)
                : loader.Folder(folder).LoadFromAssemblyName(assemblyName);
        }
    }
}
