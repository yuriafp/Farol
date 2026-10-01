using System.ComponentModel;
using Farol.Core.Text;
using Farol.Engine.Analysis;
using Farol.Engine.Toolchain;
using Farol.Engine.Workspaces;
using Farol.Tools.Infrastructure;
using ModelContextProtocol.Server;

namespace Farol.Tools.Features.Workspaces;

[McpServerToolType]
public sealed class OverviewTool(WorkspaceManager workspaces, ToolchainProbe toolchain)
{
    [McpServerTool(Name = "dotnet_overview", Title = "Map a .NET solution", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "One-call map of a C#/VB .NET solution: each project's language, format (SDK-style or classic/legacy), target frameworks, output type, " +
        "app models (ASP.NET Core, WebForms, MVC 5, Web API 2, WCF, ASMX, WinForms, WPF, EF6/EF Core), test frameworks and project references, " +
        "plus legacy markers and the build command that works for it. Call this first in an unfamiliar .NET codebase instead of opening project files.")]
    public Task<string> Run(
        [McpHeader(ToolParameters.WorkspaceHeader), Description(ToolParameters.WorkspaceDescription)] string? workspace = null,
        [Description(ToolParameters.MaxTokensDescription)] int maxTokens = TokenBudget.DefaultTokens,
        CancellationToken cancellationToken = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var session = workspaces.GetSession(workspace);
            var solution = await session.GetSolutionAsync(wait: true, cancellationToken);
            var overview = SolutionOverviewBuilder.Build(session.Target, solution, session.Report);
            var info = await toolchain.ProbeAsync(session.Target.Directory, cancellationToken);
            return OverviewRenderer.Render(overview, info, workspaces.RootDirectory, maxTokens);
        });
}
