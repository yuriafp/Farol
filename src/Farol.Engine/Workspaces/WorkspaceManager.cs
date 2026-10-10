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

    public string RootDirectory => _options.RootDirectory;

    /// <summary>Where tools may read and write; every path argument goes through it.</summary>
    public PathSandbox Paths => paths;

    /// <summary>
    /// Resolves the requested workspace (or the default one) and returns its session, creating it on first use.
    /// Workspaces outside the trusted directories are refused before anything is read or evaluated.
    /// </summary>
    public WorkspaceSession GetSession(string? workspace)
    {
        var target = ResolveTarget(workspace);
        paths.Demand(target.Path, $"Workspace '{target.DisplayName}'");
        return _sessions.GetOrAdd(
            target.Path,
            _ => new WorkspaceSession(target, loader, _options.MSBuildProperties.AsReadOnly(), loggerFactory.CreateLogger<WorkspaceSession>(), usage));
    }

    /// <summary>
    /// The folder of the workspace a call means, without creating its session, for resolving the call's paths
    /// (<see cref="PathSandbox.Resolve(string, string?)"/>); null when there is no such workspace.
    /// </summary>
    public string? TargetDirectory(string? workspace)
    {
        try
        {
            var target = ResolveTarget(workspace);
            return paths.Contains(target.Path) ? target.Directory : null;
        }
        catch (FarolException)
        {
            return null;
        }
    }

    /// <summary>
    /// The workspace a call means: the one it names, else <c>--workspace</c>, else the one discovered in the root. When
    /// the root has none or several, a call without one means the workspace already in use: agents name it once and
    /// leave the parameter out after that.
    /// </summary>
    private WorkspaceTarget ResolveTarget(string? workspace)
    {
        var requested = string.IsNullOrWhiteSpace(workspace) ? _options.DefaultWorkspace : workspace.Trim();
        if (!string.IsNullOrWhiteSpace(requested))
        {
            paths.Demand(Path.GetFullPath(requested, _options.RootDirectory), $"Workspace '{requested}'");
            return WorkspaceDiscovery.Resolve(requested, _options.RootDirectory);
        }

        try
        {
            return WorkspaceDiscovery.Resolve(null, _options.RootDirectory);
        }
        catch (FarolException ex) when (ex.Code is ErrorCodes.WorkspaceNotFound or ErrorCodes.WorkspaceAmbiguous && !_sessions.IsEmpty)
        {
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

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync();
        }

        _sessions.Clear();
    }
}
