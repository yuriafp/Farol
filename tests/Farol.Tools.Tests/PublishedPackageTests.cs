using System.Text.Json;
using Farol.Core.Execution;
using Farol.Engine.Toolchain;
using Farol.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Farol.Tools.Tests;

/// <summary>
/// The package users install: the Farol.Mcp version the plugin runs, installed from nuget.org by the Published package
/// workflow, which points FAROL_PUBLISHED_TOOL at its executable; skipped anywhere else. The checks are behavior every
/// release keeps, so the source can move ahead of the published version without failing them.
/// </summary>
public sealed class PublishedPackageTests
{
    private static readonly string? Tool = Environment.GetEnvironmentVariable("FAROL_PUBLISHED_TOOL");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_published_server_is_the_version_the_plugin_runs_with_its_tools_and_prompts()
    {
        SkipUnlessPublished();
        using var copy = FixtureCopy.Create(TestPaths.ModernDirectory);
        await using var client = await StartAsync(copy.Root, ["--autoload", "false"]);

        var tools = (await client.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).ToHashSet();
        var prompts = (await client.ListPromptsAsync(cancellationToken: Ct)).Select(p => p.Name).ToHashSet();

        Assert.Equal(("farol", PluginVersion()), (client.ServerInfo.Name, client.ServerInfo.Version));
        Assert.Superset(new HashSet<string> { "dotnet_workspace", "dotnet_overview", "dotnet_find_references", "dotnet_check", "dotnet_build", "dotnet_test" }, tools);
        Assert.Superset(new HashSet<string> { "explore", "verify_changes", "upgrade_package", "modernize" }, prompts);
    }

    [Fact]
    public async Task The_published_server_loads_the_legacy_fixture_finds_references_and_reports_a_new_error()
    {
        SkipUnlessPublished();
        using var copy = FixtureCopy.Create(TestPaths.LegacyDirectory);
        Assert.SkipUnless(
            OperatingSystem.IsWindows() && (await new ToolchainProbe(new LocalProcessRunner()).ProbeAsync(copy.Root, Ct)).PreferredVisualStudio is not null,
            "Needs Windows with Visual Studio or Build Tools.");
        await using var client = await StartAsync(copy.Root, ["--autoload", "false"]);

        await CallAsync(client, "dotnet_workspace", new() { ["action"] = "load" });
        var overview = await CallAsync(client, "dotnet_overview", []);
        var references = await CallAsync(client, "dotnet_find_references", new() { ["symbol"] = "OrderCalculator.GetTotal" });
        var calculator = copy.PathOf("Legacy.Core", "Orders", "OrderCalculator.cs");
        var source = await File.ReadAllTextAsync(calculator, Ct);
        await File.WriteAllTextAsync(calculator, source.Replace("return order.Subtotal() * (1 + TaxRate());", "return \"total\";", StringComparison.Ordinal), Ct);
        var check = await Eventually.MatchesAsync(() => CallAsync(client, "dotnet_check", []), t => t.Contains("CS0029", StringComparison.Ordinal), Ct);

        Assert.Contains("6 project(s)", overview, StringComparison.Ordinal);
        Assert.Contains("VB 1", overview, StringComparison.Ordinal);
        Assert.Contains("8 in 5 project(s)", references, StringComparison.Ordinal);
        Assert.Contains("Legacy.VbLib/ShippingCalculator.vb:", references, StringComparison.Ordinal);
        Assert.Contains("- Legacy.Core/Orders/OrderCalculator.cs:23 · error CS0029", check, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_published_server_keeps_the_usage_log_when_asked_and_reports_it()
    {
        SkipUnlessPublished();
        using var copy = FixtureCopy.Create(TestPaths.ModernDirectory);
        await FixtureRestore.EnsureRestoredAsync(copy.PathOf("Modern.slnx"), Ct);
        var log = Directory.CreateTempSubdirectory("farol-published-usage-");
        try
        {
            await using (var client = await StartAsync(copy.Root, ["--autoload", "false", "--usage-log", "--Farol:UsageLogDirectory", log.FullName]))
            {
                await CallAsync(client, "dotnet_find_symbols", new() { ["query"] = "PriceCalculator" });
            }

            var report = await new LocalProcessRunner().RunAsync(
                new ProcessSpec(Tool!, ["--usage-report", "--Farol:UsageLogDirectory", log.FullName], copy.Root, TimeSpan.FromMinutes(1)),
                Ct);

            Assert.Equal((0, ""), (report.ExitCode, report.StandardError));
            Assert.Contains($"sessions: 1, Farol {PluginVersion()} (1)", report.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("clients: farol-published-check 1.0 (1)", report.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("tool calls: 1, errors: 0", report.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                log.Delete(recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The server may still be shutting down; temp cleanup is best effort.
            }
        }
    }

    private static void SkipUnlessPublished() =>
        Assert.SkipUnless(Tool is not null, "Runs in the Published package workflow, with FAROL_PUBLISHED_TOOL set to Farol installed from nuget.org.");

    private static async Task<McpClient> StartAsync(string root, string[] options) =>
        await McpClient.CreateAsync(
            new StdioClientTransport(new StdioClientTransportOptions { Name = "farol", Command = Tool!, Arguments = ["--root", root, .. options] }),
            new McpClientOptions { ClientInfo = new Implementation { Name = "farol-published-check", Version = "1.0" } },
            cancellationToken: Ct);

    private static async Task<string> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments, cancellationToken: Ct);
        var text = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(t => t.Text));
        Assert.False(result.IsError == true, text);
        return text;
    }

    // The version the plugin runs, which the README pins too: what a user who installs Farol today gets.
    private static string PluginVersion()
    {
        using var mcp = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "plugins", "farol", ".mcp.json")));
        var package = mcp.RootElement.GetProperty("mcpServers").GetProperty("farol").GetProperty("args").EnumerateArray()
            .Select(a => a.GetString()!)
            .Single(a => a.StartsWith("Farol.Mcp@", StringComparison.Ordinal));
        return package["Farol.Mcp@".Length..];
    }
}
