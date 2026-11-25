using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Farol.Engine.Analysis;
using Farol.Engine.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farol.Engine.Indexing;

/// <summary>
/// The persistent indexes (spec 002, AC-50 – AC-53): one SQLite file per workspace under
/// <c>Farol/index/&lt;hash of the workspace's full path&gt;/</c> in the local application data folder, written after a load
/// and its warm-up and read at the next start, so navigation answers before the load is done. An index of another format
/// or that cannot be read is deleted (the next load writes a new one); one unused for 30 days is deleted at startup.
/// </summary>
public sealed partial class WorkspaceIndexes(IOptions<FarolEngineOptions> options, ILogger<WorkspaceIndexes> logger)
{
    public const string FileName = "index.db";

    /// <summary>An index nobody read or wrote for this long is deleted (AC-52).</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    // Touched on every read and write: what retention measures.
    private const string UsedMarker = "used";

    // A temporary file this old is left over from a server that stopped while writing.
    private static readonly TimeSpan TemporaryLifetime = TimeSpan.FromDays(1);

    private readonly FarolEngineOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, WorkspaceIndex> _open = new(StringComparer.Ordinal);
    private int _swept;
    private int _unavailable;

    /// <summary>
    /// False under <c>Farol:Index=false</c>, and once SQLite cannot be loaded on this machine: no index is read or written.
    /// </summary>
    public bool Enabled => _options.Index && Volatile.Read(ref _unavailable) == 0;

    public string Directory => _options.IndexDirectory is { Length: > 0 } directory
        ? Path.GetFullPath(directory)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "Farol", "index");

    /// <summary>The index file of a workspace, whether or not it exists.</summary>
    public string PathOf(WorkspaceTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var key = OperatingSystem.IsWindows() ? target.Path.ToUpperInvariant() : target.Path;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];
        return Path.Combine(Directory, hash, FileName);
    }

    /// <summary>The workspace's index, or null when there is none, indexes are off, or it is unreadable or of another format.</summary>
    public WorkspaceIndex? Open(WorkspaceTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!Enabled)
        {
            return null;
        }

        try
        {
            Sweep();
            var path = PathOf(target);
            if (_open.TryGetValue(path, out var open) && open is not null)
            {
                return open;
            }

            // Not remembered when there is none: another server may write it meanwhile.
            return Read(target) is { } read ? _open.GetOrAdd(path, read) : null;
        }
#pragma warning disable CA1031 // The index is an optimization: whatever goes wrong, answers come from the load.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            Discard(target, ex);
            return null;
        }
    }

    /// <summary>
    /// After a query of the workspace's index failed: it is not used again by this server, and deleted when it is
    /// unreadable (the next load writes a new one). Answers then come from the load.
    /// </summary>
    public void Discard(WorkspaceTarget target, Exception error)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(error);
        var path = PathOf(target);
        _open.TryRemove(path, out _);
        switch (error)
        {
            case DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or TypeInitializationException:
                Volatile.Write(ref _unavailable, 1);
                LogUnavailable(logger, error);
                break;
            case IndexReplacedException:
                // Written again (by another server) between two queries of one answer: the next call opens the new one.
                break;
            case SqliteException sqlite when !Unreadable(sqlite):
                // Busy or locked by another server's write: kept, and tried again on the next call.
                LogQueryFailed(logger, error, path);
                break;
            case SqliteException or JsonException or FormatException or InvalidCastException:
                LogDiscarded(logger, path, error.Message);
                Delete(path);
                break;
            default:
                LogQueryFailed(logger, error, path);
                break;
        }
    }

    private WorkspaceIndex? Read(WorkspaceTarget target)
    {
        var path = PathOf(target);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            if (WorkspaceIndex.Read(path) is { } index)
            {
                Touch(path);
                LogOpened(logger, target.Path, index.WrittenAt);
                return index;
            }

            LogDiscarded(logger, path, "another format version");
        }
        catch (SqliteException ex) when (Unreadable(ex))
        {
            LogDiscarded(logger, path, ex.Message);
        }

        Delete(path);
        return null;
    }

    // Corrupt, not a database, or without the tables (SQLITE_CORRUPT, SQLITE_NOTADB, SQLITE_ERROR); a busy, locked or
    // momentarily unopenable file is not.
    private static bool Unreadable(SqliteException error) => error.SqliteErrorCode is 1 or 11 or 26;

    /// <summary>The same as <see cref="WriteAsync(WorkspaceSnapshot, LoadReport?, Func{bool}?, CancellationToken)"/>, always moved into place.</summary>
    public Task WriteAsync(WorkspaceSnapshot snapshot, LoadReport? report, CancellationToken cancellationToken) =>
        WriteAsync(snapshot, report, null, cancellationToken);

    /// <summary>
    /// Writes the snapshot's index to a temporary file and moves it into place. The index then read for the workspace is
    /// this one (a server that is still answering from the previous one switches on its next call).
    /// </summary>
    /// <param name="current">
    /// Asked just before the move: false when the snapshot is no longer the workspace's (it was reloaded meanwhile), so an
    /// older index never replaces the one the newer load writes.
    /// </param>
    public async Task WriteAsync(WorkspaceSnapshot snapshot, LoadReport? report, Func<bool>? current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!Enabled)
        {
            return;
        }

        Sweep();
        var path = PathOf(snapshot.Target);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Touch(path);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        var clock = Stopwatch.StartNew();
        try
        {
            var overview = SolutionOverviewBuilder.Build(snapshot.Target, snapshot.Solution, report);
            await Task.Run(() => IndexWriter.WriteAsync(snapshot, overview, temporary, cancellationToken), cancellationToken);
            if (current is not null && !current())
            {
                return;
            }

            await MoveAsync(temporary, path, cancellationToken);
            Touch(path);
            _open.TryRemove(path, out _);
            var kilobytes = new FileInfo(path).Length / 1024;
            LogWritten(logger, snapshot.Target.Path, kilobytes, clock.Elapsed.TotalSeconds);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    // A reader may hold the old file for the moment of one query: retry briefly.
    private static async Task MoveAsync(string from, string to, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(from, to, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                await Task.Delay(100 * attempt, cancellationToken);
            }
            catch (UnauthorizedAccessException) when (attempt < 10)
            {
                await Task.Delay(100 * attempt, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Deletes indexes unused for <see cref="Retention"/> and temporary files left by a server that stopped while writing,
    /// once per server. Only folders named as this class names them, and only the files it writes in them, are touched:
    /// <c>Farol:IndexDirectory</c> may point at a folder that holds other things.
    /// </summary>
    private void Sweep()
    {
        if (Interlocked.Exchange(ref _swept, 1) == 1 || !System.IO.Directory.Exists(Directory))
        {
            return;
        }

        try
        {
            var now = DateTime.UtcNow;
            foreach (var folder in System.IO.Directory.EnumerateDirectories(Directory).Where(f => IndexFolderName().IsMatch(Path.GetFileName(f))))
            {
                var index = Path.Combine(folder, FileName);
                var marker = Path.Combine(folder, UsedMarker);
                var temporaries = System.IO.Directory.EnumerateFiles(folder, $"{FileName}.*.tmp").ToList();
                var used = new[] { marker, index }.Where(File.Exists).Select(File.GetLastWriteTimeUtc).ToList();
                if (used.Count == 0 && temporaries.Count == 0)
                {
                    continue;
                }

                var expired = used.Count == 0 || used.Max() < now - Retention;
                foreach (var temporary in temporaries.Where(t => expired || File.GetLastWriteTimeUtc(t) < now - TemporaryLifetime))
                {
                    TryDelete(temporary);
                }

                if (expired && used.Count > 0 && TryDelete(index) && TryDelete(marker))
                {
                    try
                    {
                        System.IO.Directory.Delete(folder);
                    }
                    catch (IOException)
                    {
                        // Something else is in it: left alone.
                    }

                    LogExpired(logger, folder);
                }
            }
        }
        catch (IOException ex)
        {
            LogSweepFailed(logger, ex, Directory);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogSweepFailed(logger, ex, Directory);
        }
    }

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex IndexFolderName();

    private static void Touch(string index)
    {
        try
        {
            var marker = Path.Combine(Path.GetDirectoryName(index)!, UsedMarker);
            File.WriteAllText(marker, string.Empty);
            File.SetLastWriteTimeUtc(marker, DateTime.UtcNow);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void Delete(string path)
    {
        if (TryDelete(path))
        {
            return;
        }

        LogNotDeleted(logger, path);
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Index of {Workspace} written at {WrittenAt} opened")]
    private static partial void LogOpened(ILogger logger, string workspace, DateTimeOffset writtenAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Index of {Workspace} written ({Kilobytes} KB) in {Seconds:0.0} s")]
    private static partial void LogWritten(ILogger logger, string workspace, long kilobytes, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Index {Path} ignored and deleted: {Reason}")]
    private static partial void LogDiscarded(ILogger logger, string path, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Index {Path} could not be deleted; it is ignored")]
    private static partial void LogNotDeleted(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SQLite cannot be loaded: no persistent index is read or written by this server")]
    private static partial void LogUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A query of index {Path} failed; answers come from the load")]
    private static partial void LogQueryFailed(ILogger logger, Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Old indexes under {Directory} could not be swept")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception, string directory);

    [LoggerMessage(Level = LogLevel.Information, Message = "Index folder {Folder} unused for 30 days, deleted")]
    private static partial void LogExpired(ILogger logger, string folder);
}
