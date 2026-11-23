using Farol.Core;
using Farol.Engine.Toolchain;
using Farol.Engine.Workspaces;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Options;

namespace Farol.Engine.Loading;

/// <summary>
/// Picks the loader for a workspace: MSBuildWorkspace (full fidelity) when an MSBuild here can evaluate all its projects,
/// else the heuristic loader, which reads classic .NET Framework projects without MSBuild. <c>Farol:Loader</c>
/// (auto, msbuild, heuristic) forces one.
/// </summary>
public sealed class WorkspaceLoader(MSBuildWorkspaceLoader msbuild, HeuristicWorkspaceLoader heuristic, ToolchainProbe toolchain, IOptions<FarolEngineOptions> options)
{
    public const string Auto = "auto";

    public async Task<LoadedWorkspace> LoadAsync(
        WorkspaceTarget target,
        IReadOnlyDictionary<string, string> properties,
        Action<ProjectLoadProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var choice = (options.Value.Loader ?? Auto).Trim().ToLowerInvariant();
        var useHeuristic = choice switch
        {
            HeuristicWorkspaceLoader.Name => true,
            "msbuild" => false,
            Auto => await NeedsHeuristicAsync(target, cancellationToken),
            _ => throw new FarolException(ErrorCodes.InvalidArgument, $"Farol:Loader '{options.Value.Loader}' is not a loader.", "Use auto (the default), msbuild or heuristic."),
        };

        return useHeuristic
            ? await heuristic.LoadAsync(target, properties, onProgress, cancellationToken)
            : await msbuild.LoadAsync(target, properties, onProgress, cancellationToken);
    }

    // Classic projects need the MSBuild of Visual Studio or Build Tools, which exists only on Windows.
    private async Task<bool> NeedsHeuristicAsync(WorkspaceTarget target, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows() && (await toolchain.ProbeAsync(target.Directory, cancellationToken)).PreferredVisualStudio is not null)
        {
            return false;
        }

        try
        {
            var projects = await SolutionProjects.ListAsync(target, cancellationToken);
            return projects.Any(p => File.Exists(p) && !ProjectFileInspector.Inspect(p).IsSdkStyle);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return false; // MSBuildWorkspace reports what is wrong with the solution file.
        }
    }
}
