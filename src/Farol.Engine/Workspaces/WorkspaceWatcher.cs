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

    // A source the workspace lacks joins it only when the file arrived (created, renamed or moved in): an edit to one the
    // project leaves out (Compile Remove, a stray copy) must not add it.
    private const byte Edited = 0;
    private const byte Arrived = 1;

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
            watcher.Changed += (_, e) => Record(e.FullPath, e.Name, e.ChangeType);
            watcher.Created += (_, e) => Record(e.FullPath, e.Name, e.ChangeType);
            watcher.Deleted += (_, e) => Record(e.FullPath, e.Name, e.ChangeType);
            watcher.Renamed += (_, e) =>
            {
                Record(e.OldFullPath, e.OldName, WatcherChangeTypes.Deleted);
                Record(e.FullPath, e.Name, WatcherChangeTypes.Created);
            };

            // Buffer overflow: we no longer know what changed, so the only safe answer is a reload.
            watcher.Error += (_, _) => Volatile.Write(ref _reloadRequired, 1);
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
    }

    public bool HasChanges => !_sources.IsEmpty || Volatile.Read(ref _reloadRequired) == 1 || Volatile.Read(ref _markupChanged) == 1;

    /// <param name="isDocument">Whether the workspace compiles the file; an edit to one it does not is dropped unless the file arrived.</param>
    public WorkspaceChanges Drain(Func<string, bool> isDocument)
    {
        var reload = Interlocked.Exchange(ref _reloadRequired, 0) == 1;
        var markup = Interlocked.Exchange(ref _markupChanged, 0) == 1;
        var sources = new List<string>();
        foreach (var path in _sources.Keys)
        {
            if (_sources.TryRemove(path, out var change) && (change == Arrived || isDocument(path)))
            {
                sources.Add(path);
            }
        }

        return new WorkspaceChanges(sources, reload, markup);
    }

    /// <summary>Puts back sources that could not be read yet (e.g. still locked by the writer); they already passed <see cref="Drain"/>.</summary>
    public void Requeue(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            _sources[path] = Arrived;
        }
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }
    }

    // The name is relative to the watched folder: a workspace that itself lives under a folder named bin is still watched.
    private void Record(string path, string? name, WatcherChangeTypes change)
    {
        if (IsIgnored(name ?? path))
        {
            return;
        }

        var extension = Path.GetExtension(path);
        if (SourceExtensions.Contains(extension))
        {
            var recorded = change is WatcherChangeTypes.Created ? Arrived : Edited;
            _sources.AddOrUpdate(path, recorded, (_, earlier) => Math.Max(earlier, recorded));
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

    internal static bool IsIgnored(string path) =>
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
