using System.Collections.Concurrent;
using Farol.Core;
using Farol.Core.Paths;
using Farol.Core.Usage;
using Farol.Engine.Loading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farol.Engine.Workspaces;

/// <summary>
/// Owns every workspace session, keyed by the resolved solution/project path. A call names its workspace or leaves it
/// out for the default: <c>--workspace</c>, the one discovered in the root, else the one in use in this server process.
/// Nothing lives in the protocol session, so a future HTTP gateway serving several clients routes by naming it on every call.
/// </summary>
public sealed class WorkspaceManager(MSBuildWorkspaceLoader loader, PathSandbox paths, IOptions<FarolEngineOptions> options, ILoggerFactory loggerFactory, IUsageLog usage) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, WorkspaceSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly FarolEngineOptions _options = options.Value;
    private int _unresolvedNoticeGiven;

    public string RootDirectory => _options.RootDirectory;

    /// <summary>Where tools may read and write; every path argument goes through it.</summary>
    public PathSandbox Paths => paths;

    /// <summary>
    /// Resolves the requested workspace (or the default one) and returns its session, creating it on first use.
    /// Workspaces outside the trusted directories are refused before anything is read or evaluated.
    /// </summary>
    public WorkspaceSession GetSession(string? workspace, string? file = null)
    {
        var target = ResolveTarget(workspace, file);
        paths.Demand(target.Path, $"Workspace '{target.DisplayName}'");
        return _sessions.GetOrAdd(
            target.Path,
            _ => new WorkspaceSession(target, loader, _options.MSBuildProperties.AsReadOnly(), loggerFactory.CreateLogger<WorkspaceSession>(), usage));
    }

    /// <summary>
    /// The folder of the workspace a call means, without creating its session, for resolving the call's paths
    /// (<see cref="PathSandbox.Resolve(string, string?)"/>); null when there is no such workspace.
    /// </summary>
    public string? TargetDirectory(string? workspace, string? file = null)
    {
        try
        {
            var target = ResolveTarget(workspace, file);
            return paths.Contains(target.Path) ? target.Directory : null;
        }
        catch (FarolException)
        {
            return null;
        }
    }

    /// <summary>
    /// True the first time it is asked in this server: an editor hook that cannot pick a workspace tells the agent once,
    /// rather than on every edit.
    /// </summary>
    public bool FirstUnresolvedNotice() => Interlocked.Exchange(ref _unresolvedNoticeGiven, 1) == 0;

    /// <summary>
    /// The workspace a call means: the one it names, else <c>--workspace</c>, else the one discovered in the root. When
    /// the root has none or several, a call about a file means the workspace in use that holds it, else the solution
    /// nearest above it (AC-44); otherwise the workspace already in use: agents name it once and leave the parameter
    /// out after that (AC-41).
    /// </summary>
    private WorkspaceTarget ResolveTarget(string? workspace, string? file)
    {
        var requested = string.IsNullOrWhiteSpace(workspace) ? _options.DefaultWorkspace : workspace.Trim();
        if (!string.IsNullOrWhiteSpace(requested))
        {
            paths.Demand(PathSandbox.FullPath(requested, _options.RootDirectory, "Workspace"), $"Workspace '{requested}'");
            return WorkspaceDiscovery.Resolve(requested, _options.RootDirectory);
        }

        try
        {
            return WorkspaceDiscovery.Resolve(null, _options.RootDirectory);
        }
        catch (FarolException ex) when (ex.Code is ErrorCodes.WorkspaceNotFound or ErrorCodes.WorkspaceAmbiguous)
        {
            if (FullPathOf(file) is { } fullFile)
            {
                var holding = _sessions.Values
                    .Select(s => s.Target)
                    .Where(t => fullFile.StartsWith(t.Directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    .MaxBy(t => t.Directory.Length);
                if ((holding ?? WorkspaceDiscovery.NearestAbove(fullFile, _options.RootDirectory)) is { } target)
                {
                    return target;
                }
            }

            if (_sessions.IsEmpty)
            {
                throw;
            }

            var inUse = _sessions.Values.Select(s => s.Target).OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase).ToList();
            if (inUse.Count == 1)
            {
                return inUse[0];
            }

            throw new FarolException(
                ErrorCodes.WorkspaceAmbiguous,
                $"No workspace given, and {inUse.Count} are in use: {DisplayPath.Places(_options.RootDirectory, inUse.Select(t => t.Path), max: 40)}.",
                "Pass one of them in the 'workspace' parameter.");
        }
    }

    /// <summary>
    /// The trusted full path of a file a call is about, as path arguments resolve (AC-42): absolute, else under the root,
    /// else under the folder of a workspace in use. Null when there is none, or it is not a path Farol may read.
    /// </summary>
    private string? FullPathOf(string? file)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return null;
        }

        try
        {
            var given = file.Trim();
            var fullPath = PathSandbox.FullPath(given, _options.RootDirectory);
            if (!Path.IsPathRooted(given) && !File.Exists(fullPath))
            {
                fullPath = _sessions.Values
                    .Select(s => PathSandbox.FullPath(given, s.Target.Directory))
                    .FirstOrDefault(File.Exists) ?? fullPath;
            }

            return paths.Contains(fullPath) ? fullPath : null;
        }
        catch (FarolException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync();
        }

        _sessions.Clear();
    }
}
