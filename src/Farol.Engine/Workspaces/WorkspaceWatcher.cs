using System.Collections.Concurrent;

namespace Farol.Engine.Workspaces;

public sealed record WorkspaceChanges(IReadOnlyList<string> TouchedSources, bool ReloadRequired, bool MarkupChanged)
{
    public bool IsEmpty => TouchedSources.Count == 0 && !ReloadRequired && !MarkupChanged;
}

/// <summary>
/// Records file-system changes between requests. It only collects them: the session applies them
/// lazily, right before the next request, so bursts of edits cost one snapshot update.
/// </summary>
internal sealed class WorkspaceWatcher : IDisposable
{
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase) { ".cs", ".vb" };

    private static readonly HashSet<string> ProjectExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csproj", ".vbproj", ".fsproj", ".props", ".targets", ".sln", ".slnx", ".slnf",
    };

    private static readonly HashSet<string> ProjectFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "global.json", "packages.config", "nuget.config", "Directory.Build.rsp",
    };

    private static readonly HashSet<string> IgnoredSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".idea", "node_modules",
    };

    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly ConcurrentDictionary<string, byte> _sources = new(StringComparer.OrdinalIgnoreCase);
    private int _reloadRequired;
    private int _markupChanged;

    public WorkspaceWatcher(IEnumerable<string> roots)
    {
        foreach (var root in OutermostDirectories(roots))
        {
            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Changed += (_, e) => Record(e.FullPath, e.ChangeType);
            watcher.Created += (_, e) => Record(e.FullPath, e.ChangeType);
            watcher.Deleted += (_, e) => Record(e.FullPath, e.ChangeType);
            watcher.Renamed += (_, e) =>
            {
                Record(e.OldFullPath, WatcherChangeTypes.Deleted);
                Record(e.FullPath, WatcherChangeTypes.Created);
            };

            // Buffer overflow: we no longer know what changed, so the only safe answer is a reload.
            watcher.Error += (_, _) => Volatile.Write(ref _reloadRequired, 1);
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
    }

    public bool HasChanges => !_sources.IsEmpty || Volatile.Read(ref _reloadRequired) == 1 || Volatile.Read(ref _markupChanged) == 1;

    public WorkspaceChanges Drain()
    {
        var reload = Interlocked.Exchange(ref _reloadRequired, 0) == 1;
        var markup = Interlocked.Exchange(ref _markupChanged, 0) == 1;
        var sources = new List<string>();
        foreach (var path in _sources.Keys)
        {
            if (_sources.TryRemove(path, out _))
            {
                sources.Add(path);
            }
        }

        return new WorkspaceChanges(sources, reload, markup);
    }

    /// <summary>Puts back sources that could not be read yet (e.g. still locked by the writer).</summary>
    public void Requeue(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            _sources.TryAdd(path, 0);
        }
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }
    }

    private void Record(string path, WatcherChangeTypes change)
    {
        if (IsIgnored(path))
        {
            return;
        }

        var extension = Path.GetExtension(path);
        if (SourceExtensions.Contains(extension))
        {
            _sources.TryAdd(path, 0);
        }
        else if (Markup.MarkupFiles.Extensions.Contains(extension))
        {
            Volatile.Write(ref _markupChanged, 1);
        }
        else if (ProjectExtensions.Contains(extension) || ProjectFileNames.Contains(Path.GetFileName(path)))
        {
            Volatile.Write(ref _reloadRequired, 1);
        }
        else if (extension.Length == 0 && change is WatcherChangeTypes.Deleted)
        {
            // A deleted directory takes its sources with it without per-file events.
            Volatile.Write(ref _reloadRequired, 1);
        }
    }

    private static bool IsIgnored(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(IgnoredSegments.Contains);

    private static List<string> OutermostDirectories(IEnumerable<string> roots)
    {
        var ordered = roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(r => r.Length).ToList();
        var result = new List<string>();
        foreach (var root in ordered)
        {
            if (!result.Any(r => root.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(root);
            }
        }

        return result;
    }
}
