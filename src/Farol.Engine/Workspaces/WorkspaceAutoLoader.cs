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
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.AutoLoad)
        {
            return Task.CompletedTask;
        }

        try
        {
            var session = workspaces.GetSession(null);
            _ = session.StartLoading();
            LogPreloading(logger, session.Target.Path);
        }
        catch (FarolException ex)
        {
            LogNothingToPreload(logger, ex.Message);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Preloading {Workspace}")]
    private static partial void LogPreloading(ILogger logger, string workspace);

    [LoggerMessage(Level = LogLevel.Information, Message = "No default workspace to preload: {Reason}")]
    private static partial void LogNothingToPreload(ILogger logger, string reason);
}
