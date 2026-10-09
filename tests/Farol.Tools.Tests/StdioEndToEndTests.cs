using System.Text.Json;
using System.Text.RegularExpressions;
using Farol.Core.Execution;
using Farol.Core.Usage;
using Farol.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Farol.Tools.Tests;

/// <summary>
/// AC-31 (and Spike B): the real Farol executable over stdio, as Claude Code or Visual Studio launch it.
/// Any log line written to stdout would corrupt the JSON-RPC stream and fail these tests. AC-38: the help it prints
/// for whoever runs it in a terminal. AC-39 and AC-40: the usage log it keeps when asked, and the report on it.
/// </summary>
public sealed partial class StdioEndToEndTests(ModernCopyFixture fixture) : IClassFixture<ModernCopyFixture>
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

    [Fact]
    public async Task Bare_read_only_and_offline_flags_apply_without_swallowing_the_next_switch()
    {
        var ct = TestContext.Current.CancellationToken;
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "farol",
            Command = "dotnet",
            Arguments = [HostAssemblyPath(), "--read-only", "--offline", "--root", fixture.Copy.Root, "--autoload", "false"],
        });
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct);

        var apply = await client.CallToolAsync(
            "dotnet_code_actions",
            new Dictionary<string, object?> { ["path"] = "src/Modern.Core/Pricing/PriceCalculator.cs", ["line"] = 1, ["action"] = "1", ["apply"] = true },
            cancellationToken: ct);
        var outside = await client.CallToolAsync("dotnet_outline", new Dictionary<string, object?> { ["path"] = "../Other.cs" }, cancellationToken: ct);

        Assert.Contains("runs read-only", client.ServerInstructions, StringComparison.Ordinal);
        Assert.Contains("runs offline", client.ServerInstructions, StringComparison.Ordinal);
        Assert.Equal(true, apply.IsError);
        Assert.Contains("--read-only", Text(apply), StringComparison.Ordinal);
        Assert.Equal(true, outside.IsError);
        Assert.Contains(fixture.Copy.Root.Replace('\\', '/'), Text(outside), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-?")]
    [InlineData("/?")]
    public async Task AC38_help_prints_the_version_and_every_option_and_exits_without_starting_the_server(string help)
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await new LocalProcessRunner().RunAsync(
            new ProcessSpec("dotnet", [HostAssemblyPath(), "--read-only", help], fixture.Copy.Root, TimeSpan.FromMinutes(1)),
            ct);

        // The server logs to stderr as soon as it starts, so an empty stderr means it never did.
        Assert.Equal((0, ""), (run.ExitCode, run.StandardError));
        Assert.StartsWith($"Farol {DistributionTests.Version}:", run.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($"Farol.Mcp@{DistributionTests.Version}", run.StandardOutput, StringComparison.Ordinal);
        var documented = ConfigurationSwitch().Matches(await File.ReadAllTextAsync(Path.Combine(TestPaths.RepoRoot, "README.md"), ct)).Select(m => m.Groups["switch"].Value).ToList();
        Assert.Contains("--read-only", documented);
        Assert.All(documented, option => Assert.Contains(option, run.StandardOutput, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AC39_AC40_the_usage_log_records_each_call_without_its_arguments_and_the_report_sums_it_up()
    {
        var ct = TestContext.Current.CancellationToken;
        var on = Directory.CreateTempSubdirectory("farol-usage-");
        var off = Directory.CreateTempSubdirectory("farol-usage-off-");
        try
        {
            await using (var client = await StartAsync(["--usage-log", "--Farol:UsageLogDirectory", on.FullName], ct))
            {
                await client.CallToolAsync("dotnet_workspace", new Dictionary<string, object?> { ["action"] = "load" }, cancellationToken: ct);
                var found = await client.CallToolAsync("dotnet_find_symbols", new Dictionary<string, object?> { ["query"] = "PriceCalculator" }, cancellationToken: ct);
                var missing = await client.CallToolAsync("dotnet_symbol", new Dictionary<string, object?> { ["symbol"] = "NoSuchSymbolXyz123" }, cancellationToken: ct);
                Assert.Equal((false, true), (found.IsError == true, missing.IsError == true));
            }

            await using (var client = await StartAsync(["--Farol:UsageLogDirectory", off.FullName], ct))
            {
                await client.CallToolAsync("dotnet_overview", new Dictionary<string, object?>(), cancellationToken: ct);
            }

            var log = string.Concat(on.GetFiles().Select(f => File.ReadAllText(f.FullName)));
            var events = log.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonSerializer.Deserialize<UsageEvent>(l, UsageJson)!).ToList();
            var session = Assert.Single(events, e => e.Kind == UsageEventKind.Session);
            (string?, string?, string?)[] calls =
            [
                ("dotnet_workspace", UsageOutcome.Ok, null),
                ("dotnet_find_symbols", UsageOutcome.Ok, null),
                ("dotnet_symbol", UsageOutcome.Error, "symbol_not_found"),
            ];
            var report = await new LocalProcessRunner().RunAsync(
                new ProcessSpec("dotnet", [HostAssemblyPath(), "--usage-report", "--Farol:UsageLogDirectory", on.FullName], fixture.Copy.Root, TimeSpan.FromMinutes(1)),
                ct);

            Assert.Equal(DistributionTests.Version, Assert.Single(events, e => e.Kind == UsageEventKind.Start).Farol);
            Assert.Equal(("farol-tests", "1.2.3"), (session.Client, session.ClientVersion));
            Assert.Equal(UsageEvent.HashName("Modern.slnx"), Assert.Single(events, e => e.Kind == UsageEventKind.Load).Workspace);
            Assert.Equal(calls, events.Where(e => e.Kind == UsageEventKind.Call).Select(e => (e.Tool, e.Outcome, e.Error)));
            Assert.All(
                ["PriceCalculator", "NoSuchSymbolXyz123", "Modern", fixture.Copy.Root, fixture.Copy.Root.Replace('\\', '/')],
                secret => Assert.DoesNotContain(secret, log, StringComparison.OrdinalIgnoreCase));
            Assert.Empty(off.GetFiles()); // off unless asked for
            Assert.Equal((0, ""), (report.ExitCode, report.StandardError));
            Assert.Contains("days with use: 1", report.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("clients: farol-tests 1.2.3 (1)", report.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("tool calls: 3, errors: 1", report.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("errors by code: symbol_not_found (1)", report.StandardOutput, StringComparison.Ordinal);
            Assert.Contains($"- {UsageEvent.HashName("Modern.slnx")}: 1 load(s)", report.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            DeleteQuietly(on);
            DeleteQuietly(off);
        }
    }

    private static readonly JsonSerializerOptions UsageJson = new(JsonSerializerDefaults.Web);

    private async Task<McpClient> StartAsync(string[] options, CancellationToken ct) =>
        await McpClient.CreateAsync(
            new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "farol",
                Command = "dotnet",
                Arguments = [HostAssemblyPath(), "--root", fixture.Copy.Root, "--autoload", "false", .. options],
            }),
            new McpClientOptions { ClientInfo = new Implementation { Name = "farol-tests", Version = "1.2.3" } },
            cancellationToken: ct);

    private static void DeleteQuietly(DirectoryInfo directory)
    {
        try
        {
            directory.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A server may still be shutting down; temp cleanup is best effort.
        }
    }

    private static string Text(CallToolResult result) => string.Join('\n', result.Content.OfType<TextContentBlock>().Select(t => t.Text));

    // The command-line column of the README's Configuration table.
    [GeneratedRegex(@"^\| `Farol:\w+` \| `(?<switch>--[^ `]+)", RegexOptions.Multiline)]
    private static partial Regex ConfigurationSwitch();

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
