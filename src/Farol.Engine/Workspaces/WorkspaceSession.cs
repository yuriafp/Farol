using System.Collections.Concurrent;
using System.Text;
using Farol.Core;
using Farol.Engine.Loading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace Farol.Engine.Workspaces;

public enum WorkspaceState
{
    NotLoaded,
    Loading,
    Ready,
    Failed,
}

/// <summary>
/// One solution or project. It loads once in the background and then serves immutable snapshots:
/// readers never lock, file changes produce the next snapshot right before the next request, and a
/// reload swaps everything atomically.
/// </summary>
public sealed partial class WorkspaceSession : IAsyncDisposable
{
    private const int ReadAttempts = 5;

    private readonly MSBuildWorkspaceLoader _loader;
    private readonly IReadOnlyDictionary<string, string> _properties;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, byte> _written = new(StringComparer.OrdinalIgnoreCase);

    private Task? _loadTask;
    private Task? _warmup;
    private LoadedWorkspace? _loaded;
    private WorkspaceSnapshot? _snapshot;
    private WorkspaceWatcher? _watcher;
    private Exception? _failure;
    private int _evaluationSteps;
    private DateTimeOffset _loadStartedAt;

    internal WorkspaceSession(WorkspaceTarget target, MSBuildWorkspaceLoader loader, IReadOnlyDictionary<string, string> properties, ILogger logger)
    {
        Target = target;
        _loader = loader;
        _properties = properties;
        _logger = logger;
    }

    public WorkspaceTarget Target { get; }

    public WorkspaceState State
    {
        get
        {
            lock (_gate)
            {
                if (_snapshot is not null)
                {
                    return WorkspaceState.Ready;
                }

                if (_loadTask is { IsCompleted: false })
                {
                    return WorkspaceState.Loading;
                }

                return _failure is null ? WorkspaceState.NotLoaded : WorkspaceState.Failed;
            }
        }
    }

    public WorkspaceSnapshot? CurrentSnapshot => Volatile.Read(ref _snapshot);

    public Solution? CurrentSolution => CurrentSnapshot?.Solution;

    public LoadReport? Report => Volatile.Read(ref _loaded)?.Report;

    public string? FailureMessage => Volatile.Read(ref _failure)?.Message;

    /// <summary>Project evaluation steps completed by the current load (one per project and target framework).</summary>
    public int EvaluationSteps => Volatile.Read(ref _evaluationSteps);

    public TimeSpan LoadingFor => DateTimeOffset.UtcNow - _loadStartedAt;

    /// <summary>False when file changes cannot be observed (e.g. the file watcher could not start).</summary>
    public bool TracksFileChanges => Volatile.Read(ref _watcher) is not null;

    /// <summary>Starts loading in the background unless the workspace is already loaded or loading.</summary>
    public Task StartLoading()
    {
        lock (_gate)
        {
            if (_snapshot is not null)
            {
                return Task.CompletedTask;
            }

            if (_loadTask is { IsCompleted: false } running)
            {
                return running;
            }

            _failure = null;
            _evaluationSteps = 0;
            _loadStartedAt = DateTimeOffset.UtcNow;
            _loadTask = Task.Run(() => LoadAsync(_lifetime.Token));
            return _loadTask;
        }
    }

    /// <summary>Returns an up-to-date snapshot: loads first when needed, then applies pending file changes.</summary>
    public async Task<WorkspaceSnapshot> GetSnapshotAsync(bool wait, CancellationToken cancellationToken)
    {
        if (CurrentSnapshot is null)
        {
            var loading = StartLoading();
            if (!wait && !loading.IsCompleted)
            {
                throw new FarolException(
                    ErrorCodes.WorkspaceNotReady,
                    $"Workspace '{Target.DisplayName}' is still loading ({EvaluationSteps} project evaluation step(s) so far).",
                    "Retry shortly, or call dotnet_workspace with action='load' to wait for it.");
            }

            await loading.WaitAsync(cancellationToken);
            if (CurrentSnapshot is null)
            {
                throw new FarolException(
                    ErrorCodes.WorkspaceLoadFailed,
                    $"Workspace '{Target.DisplayName}' failed to load: {FailureMessage}",
                    "Fix the cause, then call dotnet_workspace with action='reload'.");
            }
        }

        return await RefreshAsync(cancellationToken);
    }

    public async Task<Solution> GetSolutionAsync(bool wait, CancellationToken cancellationToken) =>
        (await GetSnapshotAsync(wait, cancellationToken)).Solution;

    /// <summary>
    /// Queues source files Farol itself just wrote, so the next snapshot includes them without waiting for
    /// file-system events (which arrive later and then find nothing new).
    /// </summary>
    public void NotifyChanged(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        foreach (var path in paths)
        {
            _written.TryAdd(Path.GetFullPath(path), 0);
        }
    }

    /// <summary>Discards the current snapshot and loads again, e.g. after project files changed.</summary>
    public Task ReloadAsync()
    {
        LoadedWorkspace? previous;
        WorkspaceWatcher? watcher;
        lock (_gate)
        {
            if (_loadTask is { IsCompleted: false } running)
            {
                return running;
            }

            previous = _loaded;
            watcher = _watcher;
            _loaded = null;
            _snapshot = null;
            _watcher = null;
        }

        watcher?.Dispose();
        previous?.Workspace.Dispose();
        return StartLoading();
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        if (_loadTask is not null)
        {
            await _loadTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        if (Volatile.Read(ref _warmup) is { } warmup)
        {
            await warmup.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        Volatile.Read(ref _watcher)?.Dispose();
        Volatile.Read(ref _loaded)?.Workspace.Dispose();
        _refreshGate.Dispose();
        _lifetime.Dispose();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var loaded = await _loader.LoadAsync(Target, _properties, OnProgress, cancellationToken);
            var projects = ProjectCatalog.Build(loaded.Solution, loaded.Report);
            var watcher = StartWatcher(projects);
            lock (_gate)
            {
                _loaded = loaded;
                _watcher = watcher;
                _snapshot = new WorkspaceSnapshot(loaded.Solution, 1, projects, new WorkspaceLoad(loaded.Solution, DateTimeOffset.UtcNow));
                _warmup = Task.Run(() => WarmAsync(loaded.Solution, cancellationToken), CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Disposed while loading.
        }
#pragma warning disable CA1031 // A load failure must become state the agent can read, never an unobserved task exception.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Volatile.Write(ref _failure, ex);
            LogLoadFailed(_logger, ex, Target.Path);
        }
    }

    private async Task WarmAsync(Solution solution, CancellationToken cancellationToken)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await WorkspaceWarmup.RunAsync(solution, cancellationToken);
            LogWarm(_logger, Target.Path, clock.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Disposed while warming.
        }
#pragma warning disable CA1031 // Warming is an optimization: a failure must not surface as an unobserved task exception.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogWarmFailed(_logger, ex, Target.Path);
        }
    }

    private WorkspaceWatcher? StartWatcher(ProjectCatalog projects)
    {
        try
        {
            return new WorkspaceWatcher([Target.Directory, .. projects.ProjectDirectories]);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
        {
            LogWatcherUnavailable(_logger, ex, Target.Path);
            return null;
        }
    }

    private bool HasPendingChanges => !_written.IsEmpty || Volatile.Read(ref _watcher) is { HasChanges: true };

    private async Task<WorkspaceSnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        if (!HasPendingChanges && CurrentSnapshot is { } unchanged)
        {
            return unchanged;
        }

        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            if (CurrentSnapshot is { } current)
            {
                return await ApplyPendingChangesAsync(current, cancellationToken);
            }
        }
        finally
        {
            _refreshGate.Release();
        }

        // Another request started a reload: wait for it outside the gate, which is not reentrant.
        return await GetSnapshotAsync(wait: true, cancellationToken);
    }

    private async Task<WorkspaceSnapshot> ApplyPendingChangesAsync(WorkspaceSnapshot current, CancellationToken cancellationToken)
    {
        var watcher = Volatile.Read(ref _watcher);
        var changes = DrainChanges(watcher);
        if (changes.IsEmpty)
        {
            return current;
        }

        var (solution, unreadable) = await ApplyAsync(current, changes, cancellationToken);
        if (watcher is not null)
        {
            watcher.Requeue(unreadable);
        }
        else
        {
            NotifyChanged(unreadable);
        }

        if (changes.ReloadRequired)
        {
            LogReloading(_logger, Target.Path);
            await ReloadAsync().WaitAsync(cancellationToken);
            return CurrentSnapshot ?? throw new FarolException(
                ErrorCodes.WorkspaceLoadFailed,
                $"Workspace '{Target.DisplayName}' failed to reload after project changes: {FailureMessage}",
                "Fix the project files, then call dotnet_workspace with action='reload'.");
        }

        // Files touched without new content (or written by Farol and then seen by the watcher) keep the snapshot.
        if (ReferenceEquals(solution, current.Solution) && !changes.MarkupChanged)
        {
            return current;
        }

        var next = current with { Solution = solution, Version = current.Version + 1 };
        Volatile.Write(ref _snapshot, next);
        return next;
    }

    private WorkspaceChanges DrainChanges(WorkspaceWatcher? watcher)
    {
        var written = new List<string>();
        foreach (var path in _written.Keys)
        {
            if (_written.TryRemove(path, out _))
            {
                written.Add(path);
            }
        }

        if (watcher?.Drain() is not { } watched)
        {
            return new WorkspaceChanges(written, ReloadRequired: false, MarkupChanged: false);
        }

        return written.Count == 0
            ? watched
            : watched with { TouchedSources = [.. watched.TouchedSources.Union(written, StringComparer.OrdinalIgnoreCase)] };
    }

    private static async Task<(Solution Solution, IReadOnlyList<string> Unreadable)> ApplyAsync(
        WorkspaceSnapshot current, WorkspaceChanges changes, CancellationToken cancellationToken)
    {
        var solution = current.Solution;
        var loaded = current.Load.Solution;
        var reverted = false;
        var unreadable = new List<string>();
        foreach (var path in changes.TouchedSources)
        {
            var ids = solution.GetDocumentIdsWithFilePath(path);
            var exists = File.Exists(path);
            if (ids.Length > 0 && !exists)
            {
                solution = solution.RemoveDocuments(ids);
            }
            else if (ids.Length > 0)
            {
                var previous = await solution.GetDocument(ids[0])!.GetTextAsync(cancellationToken);
                if (await ReadTextAsync(path, previous.Encoding, previous.ChecksumAlgorithm, cancellationToken) is not { } text)
                {
                    unreadable.Add(path);
                    continue;
                }

                if (text.ContentEquals(previous))
                {
                    continue;
                }

                foreach (var id in ids)
                {
                    solution = solution.WithDocumentText(id, text, PreservationMode.PreserveIdentity);
                }

                reverted |= loaded.GetDocument(ids[0]) is { } original && (await original.GetTextAsync(cancellationToken)).ContentEquals(text);
            }
            else if (exists)
            {
                // New file: SDK-style projects include it by globbing; classic projects need a project-file change (which reloads).
                var projects = current.Projects.SdkProjectsContaining(path);
                if (projects.Count == 0 || await ReadTextAsync(path, null, SourceHashAlgorithm.Sha256, cancellationToken) is not { } text)
                {
                    continue;
                }

                foreach (var projectId in projects)
                {
                    var projectDirectory = Path.GetDirectoryName(current.Projects.Get(projectId)!.FilePath)!;
                    var folders = Path.GetRelativePath(projectDirectory, Path.GetDirectoryName(path)!)
                        .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                        .Where(f => f != ".");
                    solution = solution.AddDocument(DocumentId.CreateNewId(projectId), Path.GetFileName(path), text, folders, path);
                }
            }
        }

        return (reverted ? await RebaseAsync(loaded, solution, cancellationToken) : solution, unreadable);
    }

    /// <summary>
    /// A file put back as it was at load (git checkout, an undone edit) still forks every compilation that depends on it.
    /// Rebuilt on the load solution, the snapshot shares the load's compilations wherever nothing differs any more,
    /// and becomes the load solution itself when no difference is left.
    /// </summary>
    private static async Task<Solution> RebaseAsync(Solution loaded, Solution solution, CancellationToken cancellationToken)
    {
        var rebased = loaded;
        foreach (var project in solution.GetChanges(loaded).GetProjectChanges())
        {
            if (project.GetAddedDocuments().Any() || project.GetRemovedDocuments().Any()
                || project.GetAddedAdditionalDocuments().Any() || project.GetRemovedAdditionalDocuments().Any() || project.GetChangedAdditionalDocuments().Any()
                || project.GetAddedProjectReferences().Any() || project.GetRemovedProjectReferences().Any()
                || project.GetAddedMetadataReferences().Any() || project.GetRemovedMetadataReferences().Any())
            {
                return solution;
            }

            foreach (var id in project.GetChangedDocuments(onlyGetDocumentsWithTextChanges: true))
            {
                var text = await solution.GetDocument(id)!.GetTextAsync(cancellationToken);
                if (!text.ContentEquals(await loaded.GetDocument(id)!.GetTextAsync(cancellationToken)))
                {
                    rebased = rebased.WithDocumentText(id, text, PreservationMode.PreserveIdentity);
                }
            }
        }

        return rebased;
    }

    // Writers (editors, agents) may still hold the file: retry briefly. Reuse the encoding Roslyn detected
    // at load time so legacy code-page files (e.g. Windows-1252) keep their characters.
    private static async Task<SourceText?> ReadTextAsync(string path, Encoding? encoding, SourceHashAlgorithm checksum, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= ReadAttempts; attempt++)
        {
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return SourceText.From(stream, encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), checksum);
            }
            catch (IOException) when (attempt < ReadAttempts)
            {
                await Task.Delay(50, cancellationToken);
            }
            catch (IOException)
            {
                return null;
            }
        }

        return null;
    }

    private void OnProgress(ProjectLoadProgress progress)
    {
        if (progress.Operation == ProjectLoadOperation.Evaluate)
        {
            Interlocked.Increment(ref _evaluationSteps);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to load {Workspace}")]
    private static partial void LogLoadFailed(ILogger logger, Exception exception, string workspace);

    [LoggerMessage(Level = LogLevel.Warning, Message = "File changes will not be tracked for {Workspace}")]
    private static partial void LogWatcherUnavailable(ILogger logger, Exception exception, string workspace);

    [LoggerMessage(Level = LogLevel.Information, Message = "Project files changed; reloading {Workspace}")]
    private static partial void LogReloading(ILogger logger, string workspace);

    [LoggerMessage(Level = LogLevel.Information, Message = "Compilations and search indexes of {Workspace} ready in {Seconds:N1} s")]
    private static partial void LogWarm(ILogger logger, string workspace, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Background warm-up of {Workspace} failed; queries build what they need")]
    private static partial void LogWarmFailed(ILogger logger, Exception exception, string workspace);
}
