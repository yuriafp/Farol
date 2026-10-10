using Farol.Testing;
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
        Assert.Contains("projects: 3 (4 including target-framework variants)", text, StringComparison.Ordinal);
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

/// <summary>
/// A root above several solutions, like a repos folder (AC-41, AC-42): once a call names the workspace, later calls leave
/// it out and write paths from the solution's folder.
/// </summary>
public sealed class RootAboveTheWorkspaceTests
{
    [Fact]
    public async Task AC41_AC42_after_the_load_calls_omit_the_workspace_and_write_paths_from_its_folder()
    {
        var ct = TestContext.Current.CancellationToken;
        using var repos = FixtureCopy.CreateIn(TestPaths.ModernDirectory, "Modern");
        Directory.CreateDirectory(repos.PathOf("Other"));
        await File.WriteAllTextAsync(repos.PathOf("Other", "Other.slnx"), "<Solution />", ct);
        await File.WriteAllTextAsync(repos.PathOf("Modern", "src", "Modern.Api", "web.config"), "<configuration><appSettings><add key=\"Mode\" value=\"Test\" /></appSettings></configuration>", ct);
        await FixtureRestore.EnsureRestoredAsync(repos.PathOf("Modern", "Modern.slnx"), ct);
        await using var harness = await McpHarness.StartAsync(repos.Root, ct);
        const string file = "src/Modern.Core/Pricing/PriceCalculator.cs";
        const string config = "src/Modern.Api/web.config";

        var before = await harness.CallAsync("dotnet_outline", new() { ["path"] = file }, ct);
        var configBefore = await harness.CallAsync("dotnet_config_inspect", new() { ["path"] = config }, ct);
        var load = await harness.CallAsync("dotnet_workspace", new() { ["workspace"] = "Modern/Modern.slnx", ["action"] = "load" }, ct);
        var outline = await harness.CallAsync("dotnet_outline", new() { ["path"] = file }, ct);
        var symbol = await harness.CallAsync("dotnet_symbol", new() { ["symbol"] = $"{file}:13" }, ct);
        var actions = await harness.CallAsync("dotnet_code_actions", new() { ["path"] = file, ["line"] = 13 }, ct);
        var configAfter = await harness.CallAsync("dotnet_config_inspect", new() { ["path"] = config }, ct);
        await File.WriteAllTextAsync(repos.PathOf("Modern", "src", "Modern.Core", "Fresh.cs"), "namespace Modern.Core;\n\npublic static class Fresh\n{\n    public static int One() => \"one\";\n}\n", ct);
        var check = await harness.CallAsync("dotnet_check", new() { ["path"] = "src/Modern.Core/Fresh.cs" }, ct);
        await File.WriteAllTextAsync(repos.PathOf("Modern", "src", "Modern.Core", "Edited.cs"), "namespace Modern.Core;\n\npublic static class Edited\n{\n    public static int Two() => \"two\";\n}\n", ct);
        var hook = await harness.CallAsync("dotnet_check", new() { ["edited"] = "src/Modern.Core/Edited.cs", ["format"] = "hook" }, ct);

        Assert.True(before.IsError);
        Assert.Contains("Found 2 candidates: Modern/Modern.slnx, Other/Other.slnx.", before.Text, StringComparison.Ordinal);
        Assert.True(configBefore.IsError);
        Assert.Contains("Found 2 candidates: Modern/Modern.slnx, Other/Other.slnx.", configBefore.Text, StringComparison.Ordinal);
        Assert.False(load.IsError, load.Text);
        Assert.False(outline.IsError, outline.Text);
        Assert.Contains("outline of Modern/src/Modern.Core/Pricing/PriceCalculator.cs", outline.Text, StringComparison.Ordinal);
        Assert.False(symbol.IsError, symbol.Text);
        Assert.StartsWith("class PriceCalculator\nnamespace: Modern.Core.Pricing", symbol.Text, StringComparison.Ordinal);
        Assert.False(actions.IsError, actions.Text);
        Assert.Contains("at Modern/src/Modern.Core/Pricing/PriceCalculator.cs:13", actions.Text, StringComparison.Ordinal);
        Assert.False(configAfter.IsError, configAfter.Text);
        Assert.Contains("config: Modern/src/Modern.Api/web.config", configAfter.Text, StringComparison.Ordinal);
        Assert.False(check.IsError, check.Text);
        Assert.Contains("Modern/src/Modern.Core/Fresh.cs:5 · error CS0029", check.Text, StringComparison.Ordinal);
        Assert.Contains("after the edit to Modern/src/Modern.Core/Edited.cs", hook.Text, StringComparison.Ordinal);
        Assert.Contains("Modern/src/Modern.Core/Edited.cs:5 · error CS0029", hook.Text, StringComparison.Ordinal);
    }
}
