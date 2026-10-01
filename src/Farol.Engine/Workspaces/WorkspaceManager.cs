using System.Collections.Concurrent;
using Farol.Engine.Loading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farol.Engine.Workspaces;

/// <summary>
/// Owns every workspace session, keyed by the resolved solution/project path. Tools always pass the
/// workspace explicitly (or rely on the default), which keeps state out of the protocol session and
/// lets a future HTTP gateway route calls by workspace.
/// </summary>
public sealed class WorkspaceManager(MSBuildWorkspaceLoader loader, IOptions<FarolEngineOptions> options, ILoggerFactory loggerFactory) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, WorkspaceSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly FarolEngineOptions _options = options.Value;

    public string RootDirectory => _options.RootDirectory;

    /// <summary>Resolves the requested workspace (or the default one) and returns its session, creating it on first use.</summary>
    public WorkspaceSession GetSession(string? workspace)
    {
        var requested = string.IsNullOrWhiteSpace(workspace) ? _options.DefaultWorkspace : workspace;
        var target = WorkspaceDiscovery.Resolve(requested, _options.RootDirectory);
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
