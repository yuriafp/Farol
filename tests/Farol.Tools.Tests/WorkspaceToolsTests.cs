using Xunit;

namespace Farol.Tools.Tests;

public sealed class WorkspaceToolsTests(ModernCopyFixture fixture) : IClassFixture<ModernCopyFixture>
{
    [Fact]
    public async Task AC2_overview_maps_the_modern_fixture_in_one_call()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await McpHarness.StartAsync(fixture.Copy.Root, ct);

        var (isError, text) = await harness.CallAsync("dotnet_overview", [], ct);

        Assert.False(isError, text);
        Assert.Contains("solution: Modern.slnx", text, StringComparison.Ordinal);
        Assert.Contains("Modern.Api · C# · sdk · net10.0 · exe · ASP.NET Core", text, StringComparison.Ordinal);
        Assert.Contains("Modern.Core · C# · sdk · net10.0;net48 · library", text, StringComparison.Ordinal);
        Assert.Contains("central package management", text, StringComparison.Ordinal);
        Assert.Contains("build: dotnet build \"Modern.slnx\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Workspace_load_reports_projects_and_toolchain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await McpHarness.StartAsync(fixture.Copy.Root, ct);

        var (isError, text) = await harness.CallAsync("dotnet_workspace", new() { ["action"] = "load" }, ct);

        Assert.False(isError, text);
        Assert.Contains("state: ready", text, StringComparison.Ordinal);
        Assert.Contains("projects: 2 (3 including target-framework variants)", text, StringComparison.Ordinal);
        Assert.Contains("toolchain: .NET SDK", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_workspace_is_an_actionable_tool_error()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await McpHarness.StartAsync(fixture.Copy.Root, ct);

        var (isError, text) = await harness.CallAsync("dotnet_overview", new() { ["workspace"] = "missing/App.sln" }, ct);

        Assert.True(isError);
        Assert.Contains("was not found", text, StringComparison.Ordinal);
        Assert.Contains("Pass a .sln", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_action_is_rejected_with_the_valid_options()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await McpHarness.StartAsync(fixture.Copy.Root, ct);

        var (isError, text) = await harness.CallAsync("dotnet_workspace", new() { ["action"] = "explode" }, ct);

        Assert.True(isError);
        Assert.Contains("status, load or reload", text, StringComparison.Ordinal);
    }
}
