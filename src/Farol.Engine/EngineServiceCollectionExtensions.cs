using Farol.Core;
using Farol.Core.Execution;
using Farol.Core.Paths;
using Farol.Engine.Building;
using Farol.Engine.Loading;
using Farol.Engine.Packages;
using Farol.Engine.Testing;
using Farol.Engine.Toolchain;
using Farol.Engine.Workspaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

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
        services.AddSingleton(sp =>
        {
            var o = sp.GetRequiredService<IOptions<FarolEngineOptions>>().Value;
            return new PathSandbox(o.RootDirectory, o.TrustedPaths);
        });
        services.AddSingleton(sp => new ServerPermissions(sp.GetRequiredService<IOptions<FarolEngineOptions>>().Value.ReadOnly));
        services.AddSingleton<ToolchainProbe>();
        services.AddSingleton<MSBuildWorkspaceLoader>();
        services.AddSingleton<WorkspaceManager>();
        services.AddSingleton<BuildRunner>();
        services.AddSingleton<TestRunner>();
        services.AddSingleton<PackageFeeds>();
        services.AddHostedService<WorkspaceAutoLoader>();
        return services;
    }
}
