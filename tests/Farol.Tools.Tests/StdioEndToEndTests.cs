using Farol.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Farol.Tools.Tests;

/// <summary>
/// AC-31 (and Spike B): the real Farol executable over stdio, as Claude Code or Visual Studio launch it.
/// Any log line written to stdout would corrupt the JSON-RPC stream and fail these tests.
/// </summary>
public sealed class StdioEndToEndTests(ModernCopyFixture fixture) : IClassFixture<ModernCopyFixture>
{
    [Fact]
    public async Task AC31_the_host_answers_over_stdio_with_a_clean_stdout()
    {
        var ct = TestContext.Current.CancellationToken;
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "farol",
            Command = "dotnet",
            Arguments = [HostAssemblyPath(), "--root", fixture.Copy.Root, "--autoload", "true"],
        });
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct);

        Assert.Equal("farol", client.ServerInfo.Name);
        Assert.Contains("dotnet_overview", client.ServerInstructions, StringComparison.Ordinal);

        var tools = await client.ListToolsAsync(cancellationToken: ct);
        Assert.Contains(tools, t => t.Name == "dotnet_overview");

        var result = await client.CallToolAsync("dotnet_overview", new Dictionary<string, object?>(), cancellationToken: ct);
        var text = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(t => t.Text));
        Assert.NotEqual(true, result.IsError);
        Assert.Contains("Modern.Api", text, StringComparison.Ordinal);
    }

    // The host is built alongside the tests (see the project reference); run the same configuration.
    private static string HostAssemblyPath()
    {
        // BaseDirectory is ".../bin/<configuration>/net10.0/" (with a trailing separator).
        var targetFrameworkDirectory = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var configuration = targetFrameworkDirectory.Parent!.Name;
        var path = Path.Combine(TestPaths.RepoRoot, "src", "Farol.Host", "bin", configuration, "net10.0", "Farol.Host.dll");
        Assert.True(File.Exists(path), $"Build Farol.Host first: {path} not found.");
        return path;
    }
}
