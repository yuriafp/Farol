using System.ComponentModel;
using Farol.Core;
using Farol.Core.Text;
using Farol.Engine.Building;
using Farol.Engine.Workspaces;
using Farol.Tools.Infrastructure;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Farol.Tools.Features.BuildAndTest;

[McpServerToolType]
public sealed partial class BuildTool(WorkspaceManager workspaces, BuildRunner builds, ServerPermissions permissions, CallerContext caller, ILogger<BuildTool> logger)
{
    [McpServerTool(Name = "dotnet_build", Title = "Build a .NET solution or project", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Builds with the toolchain that works for the code — dotnet build for SDK-style projects, Visual Studio's MSBuild (restoring packages.config) " +
        "for classic .NET Framework ones — and returns the outcome with the errors and warnings read from the binary log, deduplicated across target " +
        "frameworks and grouped by project. To find the compiler errors an edit introduced, dotnet_check is much faster.")]
    public Task<string> Run(
        [Description("A project to build instead of the whole workspace, by name as dotnet_overview lists it; the projects it references are built too.")] string? project = null,
        [Description("Build configuration. Default: Debug.")] string configuration = "Debug",
        [McpHeader(ToolParameters.WorkspaceHeader), Description(ToolParameters.WorkspaceDescription)] string? workspace = null,
        [Description(ToolParameters.MaxTokensDescription)] int maxTokens = TokenBudget.DefaultTokens,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default) =>
        ToolGuard.RunAsync(async () =>
        {
            permissions.Demand("build", "Use dotnet_check to find compiler errors without building.");
            var session = workspaces.GetSession(workspace);
            var snapshot = await session.GetSnapshotAsync(wait: true, cancellationToken);
            var projectFile = project is null ? null : ProjectFile(snapshot, project);
            LogBuild(logger, projectFile ?? session.Target.Path, caller.User, caller.Origin);
            var build = await builds.BuildAsync(snapshot, session.Target, projectFile, configuration.Trim(), new ToolProgress(progress), cancellationToken);

            var text = new ResponseBuilder(maxTokens);
            BuildRenderer.Render(text, build, workspaces.RootDirectory);
            return text.ToString();
        });

    internal static string ProjectFile(WorkspaceSnapshot snapshot, string name)
    {
        var variants = snapshot.Projects.FindByName(name);
        return variants.Count > 0
            ? variants[0].FilePath
            : throw new FarolException(ErrorCodes.InvalidArgument, $"No project named '{name}'.", $"Projects: {string.Join(", ", snapshot.Projects.Names.Take(40))}.");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Build of {Path} requested by {User} via {Origin}")]
    private static partial void LogBuild(ILogger logger, string path, string user, string origin);
}
