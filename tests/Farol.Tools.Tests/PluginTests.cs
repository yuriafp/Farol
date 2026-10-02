using System.Text.Json;
using System.Text.Json.Nodes;
using Farol.Testing;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Farol.Tools.Tests;

/// <summary>
/// The Claude Code plugin (plugins/farol) and what it relies on: the hook's call to dotnet_check in hook format, and
/// the MCP prompts.
/// </summary>
public sealed class PluginTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string PluginRoot => Path.Combine(TestPaths.RepoRoot, "plugins", "farol");

    [Fact]
    public async Task The_hook_calls_dotnet_check_with_parameters_the_tool_declares()
    {
        await using var harness = await McpHarness.StartAsync(TestPaths.ModernDirectory, Ct);
        var check = (await harness.Client.ListToolsAsync(cancellationToken: Ct)).Single(t => t.Name == "dotnet_check");
        var parameters = check.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var plugin = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(PluginRoot, ".claude-plugin", "plugin.json"), Ct))!;
        var servers = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(PluginRoot, ".mcp.json"), Ct))!["mcpServers"]!.AsObject();
        var hooks = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(PluginRoot, "hooks", "hooks.json"), Ct))!["hooks"]!["PostToolUse"]!.AsArray()
            .SelectMany(m => m!["hooks"]!.AsArray())
            .ToList();

        Assert.Equal(["Edit(**/*.cs)", "Edit(**/*.vb)"], hooks.Select(h => (string?)h!["if"]));
        Assert.All(hooks, hook =>
        {
            Assert.Equal("mcp_tool", (string?)hook!["type"]);
            Assert.Equal($"plugin:{(string?)plugin["name"]}:{servers.Single().Key}", (string?)hook["server"]);
            Assert.Equal("dotnet_check", (string?)hook["tool"]);
            Assert.All(hook["input"]!.AsObject(), input => Assert.Contains(input.Key, parameters));
            Assert.Equal("hook", (string?)hook["input"]!["format"]);
            Assert.Equal("${tool_input.file_path}", (string?)hook["input"]!["edited"]);
        });
    }

    [Fact]
    public async Task AC33_in_hook_format_new_errors_block_new_warnings_inform_and_clean_edits_say_nothing()
    {
        using var copy = FixtureCopy.Create(TestPaths.ModernDirectory);
        await FixtureRestore.EnsureRestoredAsync(copy.PathOf("Modern.slnx"), Ct);
        await using var harness = await McpHarness.StartAsync(copy.Root, Ct);
        await harness.CallAsync("dotnet_workspace", new() { ["action"] = "load" }, Ct);
        var file = copy.PathOf("src", "Modern.Core", "Pricing", "PriceCalculator.cs");

        var clean = await Hook(harness, file);
        var notCode = await Hook(harness, copy.PathOf("Modern.slnx"));
        var outside = await Hook(harness, Path.Combine(Path.GetTempPath(), "Elsewhere.cs"));
        await BuildToolTests.Edit(file, "var subtotal = prices.Sum();", "var subtotal = prices.Sum(); var unused = 1;");
        var warning = JsonNode.Parse(await Hook(harness, file))!;
        await BuildToolTests.Edit(file, "var subtotal = prices.Sum();", "var subtotal = prices.Sum() + \"x\";");
        var error = JsonNode.Parse(await Hook(harness, file))!;

        Assert.Equal("{}", clean);
        Assert.Equal("{}", notCode);
        Assert.Equal("{}", outside);
        Assert.Equal("PostToolUse", (string?)warning["hookSpecificOutput"]!["hookEventName"]);
        Assert.Contains("found new compiler warnings after the edit to src/Modern.Core/Pricing/PriceCalculator.cs", (string?)warning["hookSpecificOutput"]!["additionalContext"], StringComparison.Ordinal);
        Assert.Contains("warning CS0219", (string?)warning["hookSpecificOutput"]!["additionalContext"], StringComparison.Ordinal);
        Assert.Equal("block", (string?)error["decision"]);
        Assert.Contains("found new compiler errors after the edit to src/Modern.Core/Pricing/PriceCalculator.cs", (string?)error["reason"], StringComparison.Ordinal);
        Assert.Contains("src/Modern.Core/Pricing/PriceCalculator.cs:19 · error CS0019", (string?)error["reason"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hook_format_does_not_wait_for_a_workspace_that_is_still_loading()
    {
        using var copy = FixtureCopy.Create(TestPaths.ModernDirectory);
        await using var harness = await McpHarness.StartAsync(copy.Root, Ct);

        var text = await Hook(harness, copy.PathOf("src", "Modern.Core", "Pricing", "PriceCalculator.cs"));

        Assert.Equal("{}", text);
    }

    [Fact]
    public async Task Prompts_chain_the_tools_for_common_tasks()
    {
        await using var harness = await McpHarness.StartAsync(TestPaths.ModernDirectory, Ct);

        var prompts = await harness.Client.ListPromptsAsync(cancellationToken: Ct);
        var modernize = await harness.Client.GetPromptAsync("modernize", new Dictionary<string, object?> { ["targetFramework"] = "net8.0" }, cancellationToken: Ct);
        var upgrade = await harness.Client.GetPromptAsync("upgrade_package", new Dictionary<string, object?> { ["package"] = "Newtonsoft.Json" }, cancellationToken: Ct);

        Assert.Equal(["explore", "modernize", "upgrade_package", "verify_changes"], prompts.Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Contains(prompts.Single(p => p.Name == "upgrade_package").ProtocolPrompt.Arguments!, a => a.Name == "package" && a.Required == true);
        var modernizeText = Assert.IsType<TextContentBlock>(Assert.Single(modernize.Messages).Content).Text;
        Assert.Contains("dotnet_portability targetFramework=net8.0", modernizeText, StringComparison.Ordinal);
        Assert.Contains("dotnet_migration_plan targetFramework=net8.0: the ordered steps", modernizeText, StringComparison.Ordinal);
        Assert.Contains("Newtonsoft.Json to its newest stable version", Assert.IsType<TextContentBlock>(Assert.Single(upgrade.Messages).Content).Text, StringComparison.Ordinal);
    }

    private static async Task<string> Hook(McpHarness harness, string edited)
    {
        var (isError, text) = await harness.CallAsync("dotnet_check", new() { ["format"] = "hook", ["edited"] = edited }, Ct);
        Assert.False(isError, text);
        return text;
    }
}
