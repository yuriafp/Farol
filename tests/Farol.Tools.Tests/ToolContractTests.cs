using Farol.Testing;
using Xunit;

namespace Farol.Tools.Tests;

/// <summary>Conventions every tool must keep: agents and gateways depend on them.</summary>
public sealed class ToolContractTests
{
    [Fact]
    public async Task Every_tool_is_prefixed_annotated_and_mirrors_the_workspace_into_a_header()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await McpHarness.StartAsync(TestPaths.ModernDirectory, ct);

        var tools = await harness.Client.ListToolsAsync(cancellationToken: ct);

        Assert.Contains(tools, t => t.Name == "dotnet_workspace");
        Assert.Contains(tools, t => t.Name == "dotnet_overview");
        Assert.True(tools.Single(t => t.Name == "dotnet_check").ProtocolTool.Annotations?.ReadOnlyHint);
        var codeActions = tools.Single(t => t.Name == "dotnet_code_actions").ProtocolTool.Annotations;
        Assert.False(codeActions?.ReadOnlyHint);
        Assert.True(codeActions?.DestructiveHint);
        Assert.All(tools.Where(t => t.Name is "dotnet_build" or "dotnet_test"), t => Assert.False(t.ProtocolTool.Annotations?.ReadOnlyHint));
        Assert.All(tools.Where(t => t.Name is "dotnet_packages" or "dotnet_package_api"), t =>
        {
            Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint);
            Assert.True(t.ProtocolTool.Annotations?.OpenWorldHint);
        });
        Assert.Equal(2, tools.Count(t => t.Name is "dotnet_packages" or "dotnet_package_api"));
        var migrationPlan = tools.Single(t => t.Name == "dotnet_migration_plan").ProtocolTool.Annotations;
        Assert.False(migrationPlan?.ReadOnlyHint);
        Assert.True(migrationPlan?.DestructiveHint);
        Assert.All(tools.Where(t => t.Name is "dotnet_legacy_inventory" or "dotnet_config_inspect" or "dotnet_portability"), t => Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint));
        Assert.Equal(18, tools.Count);
        Assert.All(tools, tool =>
        {
            Assert.StartsWith("dotnet_", tool.Name, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
            Assert.NotNull(tool.ProtocolTool.Annotations?.ReadOnlyHint);
            var workspace = tool.JsonSchema.GetProperty("properties").GetProperty("workspace");
            Assert.Equal("Workspace", workspace.GetProperty("x-mcp-header").GetString());
        });
    }
}
