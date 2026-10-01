using System.Collections.Concurrent;
using Farol.Core.Paths;
using Farol.Engine.Loading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farol.Engine.Workspaces;

/// <summary>
/// Owns every workspace session, keyed by the resolved solution/project path. Tools always pass the
/// workspace explicitly (or rely on the default), which keeps state out of the protocol session and
/// lets a future HTTP gateway route calls by workspace.
/// </summary>
public sealed class WorkspaceManager(MSBuildWorkspaceLoader loader, PathSandbox paths, IOptions<FarolEngineOptions> options, ILoggerFactory loggerFactory) : IAsyncDisposable
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
        var requested = string.IsNullOrWhiteSpace(workspace) ? _options.DefaultWorkspace : workspace.Trim();
        if (!string.IsNullOrWhiteSpace(requested))
        {
            paths.Demand(Path.GetFullPath(requested, _options.RootDirectory), $"Workspace '{requested}'");
        }

        var target = WorkspaceDiscovery.Resolve(requested, _options.RootDirectory);
        paths.Demand(target.Path, $"Workspace '{target.DisplayName}'");
        return _sessions.GetOrAdd(
            target.Path,
            _ => new WorkspaceSession(target, loader, _options.MSBuildProperties.AsReadOnly(), loggerFactory.CreateLogger<WorkspaceSession>()));
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
