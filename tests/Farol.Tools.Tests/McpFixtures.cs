using Farol.Core.Execution;
using Farol.Engine.Toolchain;
using Farol.Testing;
using Xunit;

namespace Farol.Tools.Tests;

/// <summary>
/// A restored copy of the modern fixture. This test process never touches tests/fixtures directly, so it
/// cannot race with the engine tests' design-time builds writing obj/ there.
/// </summary>
public sealed class ModernCopyFixture : IAsyncLifetime
{
    public FixtureCopy Copy { get; } = FixtureCopy.Create(TestPaths.ModernDirectory);

    public async ValueTask InitializeAsync() =>
        await FixtureRestore.EnsureRestoredAsync(Copy.PathOf("Modern.slnx"), CancellationToken.None);

    public ValueTask DisposeAsync()
    {
        Copy.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>A copy of the legacy fixture behind one MCP server, loaded once for every test in the class.</summary>
public sealed class LegacyMcpFixture : IAsyncLifetime
{
    public FixtureCopy Copy { get; } = FixtureCopy.Create(TestPaths.LegacyDirectory);

    public McpHarness? Harness { get; private set; }

    public bool Available => Harness is not null;

    public async ValueTask InitializeAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var toolchain = await new ToolchainProbe(new LocalProcessRunner()).ProbeAsync(Copy.Root, CancellationToken.None);
        if (toolchain.PreferredVisualStudio is null)
        {
            return;
        }

        Harness = await McpHarness.StartAsync(Copy.Root, CancellationToken.None);
        await Harness.CallAsync("dotnet_workspace", new() { ["action"] = "load" }, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (Harness is not null)
        {
            await Harness.DisposeAsync();
        }

        Copy.Dispose();
    }
}
