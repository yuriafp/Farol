using Farol.Core.Execution;
using Farol.Engine;
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

/// <summary>
/// A private copy of the legacy fixture behind its own loaded MCP server, for tests that edit files: each one
/// starts from the fixture as committed, so the load baseline is the same for all of them.
/// </summary>
public sealed class LegacyServer : IAsyncDisposable
{
    private LegacyServer(FixtureCopy copy, McpHarness? harness)
    {
        Copy = copy;
        Harness = harness;
    }

    public FixtureCopy Copy { get; }

    public McpHarness? Harness { get; }

    public bool Available => Harness is not null;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<LegacyServer> StartAsync(Action<FarolEngineOptions>? configure = null)
    {
        var copy = FixtureCopy.Create(TestPaths.LegacyDirectory);
        if (!OperatingSystem.IsWindows() || (await new ToolchainProbe(new LocalProcessRunner()).ProbeAsync(copy.Root, Ct)).PreferredVisualStudio is null)
        {
            return new LegacyServer(copy, null);
        }

        var harness = await McpHarness.StartAsync(copy.Root, Ct, configure);
        await harness.CallAsync("dotnet_workspace", new() { ["action"] = "load" }, Ct);
        return new LegacyServer(copy, harness);
    }

    public async Task<string> CallAsync(string tool, Dictionary<string, object?> arguments)
    {
        var (isError, text) = await Harness!.CallAsync(tool, arguments, Ct);
        Assert.False(isError, text);
        return text;
    }

    /// <summary>File edits reach the server through its file watcher: call until the answer reflects them.</summary>
    public async Task<string> CallUntilAsync(string tool, Dictionary<string, object?> arguments, Func<string, bool> condition)
    {
        var text = await Eventually.MatchesAsync(() => CallAsync(tool, arguments), condition, Ct);
        Assert.True(condition(text), text);
        return text;
    }

    public async Task EditAsync(string[] path, params (string Old, string New)[] replacements)
    {
        var file = Copy.PathOf(path);
        var content = await File.ReadAllTextAsync(file, Ct);
        foreach (var (old, replacement) in replacements)
        {
            Assert.Contains(old, content, StringComparison.Ordinal);
            content = content.Replace(old, replacement, StringComparison.Ordinal);
        }

        await File.WriteAllTextAsync(file, content, Ct);
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
