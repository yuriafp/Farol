using Farol.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farol.Engine.Workspaces;

/// <summary>
/// Starts loading the default workspace as soon as the server starts, so the first real question
/// finds it warm. Never blocks startup: the MCP handshake must answer immediately.
/// </summary>
public sealed partial class WorkspaceAutoLoader(WorkspaceManager workspaces, IOptions<FarolEngineOptions> options, ILogger<WorkspaceAutoLoader> logger) : IHostedService
{
    private Task _preload = Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Value.AutoLoad)
        {
            // Discovery lists folders, which from a drive root takes up to a second on a cold disk.
            _preload = Task.Run(Preload, CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => _preload.WaitAsync(cancellationToken);

    /// <summary>Discovers the default workspace and starts loading it; whatever stops it is logged, never thrown.</summary>
    internal void Preload()
    {
        try
        {
            var session = workspaces.GetSession(null);
            _ = session.StartLoading();
            LogPreloading(logger, session.Target.Path);
        }
#pragma warning disable CA1031 // Preloading is an optimization: whatever stops it, the server must still start and answer.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogNothingToPreload(logger, workspaces.RootDirectory, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Preloading {Workspace}")]
    private static partial void LogPreloading(ILogger logger, string workspace);

    [LoggerMessage(Level = LogLevel.Information, Message = "No default workspace to preload in {Root}: {Reason}")]
    private static partial void LogNothingToPreload(ILogger logger, string root, string reason);
}
