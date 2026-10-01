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
