using Farol.Core.Execution;
using Farol.Engine.Loading;
using Farol.Engine.Toolchain;
using Farol.Engine.Workspaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Farol.Engine;

public static class EngineServiceCollectionExtensions
{
    public static IServiceCollection AddFarolEngine(this IServiceCollection services, Action<FarolEngineOptions>? configure = null)
    {
        var options = services.AddOptions<FarolEngineOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        options.PostConfigure(o => o.RootDirectory = Path.GetFullPath(o.RootDirectory));

        // Hosts may replace the runner (e.g. a sandboxed runner on a shared server).
        services.TryAddSingleton<IProcessRunner, LocalProcessRunner>();
        services.AddSingleton<ToolchainProbe>();
        services.AddSingleton<MSBuildWorkspaceLoader>();
        services.AddSingleton<WorkspaceManager>();
        services.AddHostedService<WorkspaceAutoLoader>();
        return services;
    }
}
