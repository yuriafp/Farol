using Farol.Core.Usage;
using Farol.Engine.Toolchain;
using Farol.Engine.Workspaces;
using Microsoft.Extensions.DependencyInjection;

namespace Farol.Engine.Tests;

/// <summary>The engine wired exactly like a host wires it, minus MCP.</summary>
internal sealed class EngineHarness : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private EngineHarness(ServiceProvider services)
    {
        _services = services;
    }

    public WorkspaceManager Workspaces => _services.GetRequiredService<WorkspaceManager>();

    public ToolchainProbe Toolchain => _services.GetRequiredService<ToolchainProbe>();

    public static EngineHarness Create(string rootDirectory, IUsageLog? usage = null) =>
        new(new ServiceCollection()
            .AddLogging()
            .AddSingleton(usage ?? NullUsageLog.Instance)
            .AddFarolEngine(o =>
            {
                o.RootDirectory = rootDirectory;
                o.AutoLoad = false;
            })
            .BuildServiceProvider());

    public ValueTask DisposeAsync() => _services.DisposeAsync();
}
