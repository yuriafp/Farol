using System.ComponentModel;
using Farol.Core;
using Farol.Engine.Toolchain;
using Farol.Engine.Workspaces;
using Farol.Tools.Infrastructure;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Farol.Tools.Features.Workspaces;

[McpServerToolType]
public sealed partial class WorkspaceTool(WorkspaceManager workspaces, ToolchainProbe toolchain, CallerContext caller, ILogger<WorkspaceTool> logger)
{
    [McpServerTool(Name = "dotnet_workspace", Title = "Load or inspect a .NET workspace", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Loads a C#/VB .NET solution — legacy .NET Framework (classic csproj/vbproj) or modern .NET — and reports its state: " +
        "projects loaded, failures and why, target frameworks, and the MSBuild/Visual Studio toolchain in use. " +
        "Source and project-file edits are picked up automatically; action='reload' forces a fresh load. Other dotnet_* tools load the workspace on demand, so calling this first is optional.")]
    public Task<string> Run(
        [McpHeader(ToolParameters.WorkspaceHeader), Description(ToolParameters.WorkspaceDescription)] string? workspace = null,
        [Description("status (default): report without waiting, starting a background load if needed · load: wait until loaded · reload: discard and load again")] string action = "status",
        CancellationToken cancellationToken = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var session = workspaces.GetSession(workspace);
            switch (action.Trim().ToUpperInvariant())
            {
                case "STATUS":
                    _ = session.StartLoading();
                    break;
                case "LOAD":
                    await session.GetSolutionAsync(wait: true, cancellationToken);
                    break;
                case "RELOAD":
                    LogReload(logger, session.Target.Path, caller.User, caller.Origin);
                    await session.ReloadAsync().WaitAsync(cancellationToken);
                    break;
                default:
                    throw new FarolException(ErrorCodes.InvalidArgument, $"Unknown action '{action}'.", "Use status, load or reload.");
            }

            var info = await toolchain.ProbeAsync(session.Target.Directory, cancellationToken);
            return WorkspaceStatusRenderer.Render(session, info, workspaces.RootDirectory);
        });

    [LoggerMessage(Level = LogLevel.Information, Message = "Reload of {Workspace} requested by {User} via {Origin}")]
    private static partial void LogReload(ILogger logger, string workspace, string user, string origin);
}
