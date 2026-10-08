using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Farol.Core.Execution;
using Farol.Testing;
using ModelContextProtocol.Client;
using Xunit;

namespace Farol.Tools.Tests;

/// <summary>
/// What ships: the Farol.Mcp package (a .NET tool of type McpServer with .mcp/server.json), the version every
/// distribution file repeats, and the MCP surface clients and the plugin depend on.
/// </summary>
public sealed partial class DistributionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static string Version => XDocument.Load(Path.Combine(TestPaths.RepoRoot, "Directory.Build.props")).Descendants("Version").Single().Value;

    [Fact]
    public async Task Every_distribution_file_names_the_same_version()
    {
        var serverJson = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(TestPaths.RepoRoot, "src", "Farol.Host", ".mcp", "server.json"), Ct))!;
        var plugin = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(TestPaths.RepoRoot, "plugins", "farol", ".claude-plugin", "plugin.json"), Ct))!;
        var pins = new[] { Path.Combine("plugins", "farol", ".mcp.json"), Path.Combine("src", "Farol.Host", "README.md"), "README.md" }
            .SelectMany(file => DnxPin().Matches(File.ReadAllText(Path.Combine(TestPaths.RepoRoot, file))).Select(m => (File: file, Version: m.Groups["version"].Value)))
            .ToList();

        Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$", Version);
        Assert.Equal(Version, (string?)serverJson["version"]);
        var package = Assert.Single(serverJson["packages"]!.AsArray())!;
        Assert.Equal(("nuget", "Farol.Mcp", Version, "stdio"), ((string?)package["registryType"], (string?)package["identifier"], (string?)package["version"], (string?)package["transport"]!["type"]));
        Assert.Equal(Version, (string?)plugin["version"]);
        Assert.Contains(pins, p => p.File.EndsWith(".mcp.json", StringComparison.Ordinal));
        Assert.All(pins, p => Assert.True(p.Version == Version, $"{p.File} pins Farol.Mcp@{p.Version}, not {Version}."));
    }

    [Fact]
    public async Task AC34_the_package_is_an_mcp_server_tool_that_starts_from_its_own_files()
    {
        var output = Directory.CreateTempSubdirectory("farol-pack-");
        try
        {
            var pack = await new LocalProcessRunner().RunAsync(
                new ProcessSpec("dotnet", ["pack", Path.Combine(TestPaths.RepoRoot, "src", "Farol.Host", "Farol.Host.csproj"), "-c", Configuration, "--no-build", "-o", output.FullName, "-nologo"], TestPaths.RepoRoot, TimeSpan.FromMinutes(5)),
                Ct);
            Assert.True(pack.ExitCode == 0, pack.StandardOutput + pack.StandardError);
            var nupkg = Assert.Single(Directory.GetFiles(output.FullName, "*.nupkg"));
            Assert.Equal($"Farol.Mcp.{Version}.nupkg", Path.GetFileName(nupkg));

            var tool = output.CreateSubdirectory("tool").FullName;
            using (var zip = ZipFile.OpenRead(nupkg))
            {
                var entries = zip.Entries.Select(e => e.FullName).ToList();
                using var nuspec = new StreamReader(zip.Entries.Single(e => e.FullName.EndsWith(".nuspec", StringComparison.Ordinal)).Open());
                var types = XDocument.Parse(await nuspec.ReadToEndAsync(Ct)).Descendants().Where(e => e.Name.LocalName == "packageType").Select(e => (string?)e.Attribute("name"));
                Assert.Equal(["DotnetTool", "McpServer"], types.Order(StringComparer.Ordinal));
                Assert.Contains(".mcp/server.json", entries);
                Assert.Contains("README.md", entries);
                Assert.Contains("tools/net10.0/any/Farol.Host.dll", entries);
                Assert.Contains(entries, e => e.StartsWith("tools/net10.0/any/BuildHost-netcore/", StringComparison.Ordinal));
                Assert.Contains(entries, e => e.StartsWith("tools/net10.0/any/BuildHost-net472/", StringComparison.Ordinal));
                using var packed = new StreamReader(zip.GetEntry(".mcp/server.json")!.Open());
                Assert.Equal(
                    (await File.ReadAllTextAsync(Path.Combine(TestPaths.RepoRoot, "src", "Farol.Host", ".mcp", "server.json"), Ct)).ReplaceLineEndings(),
                    (await packed.ReadToEndAsync(Ct)).ReplaceLineEndings());
                foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("tools/net10.0/any/", StringComparison.Ordinal) && e.Name.Length > 0))
                {
                    var target = Path.Combine(tool, entry.FullName["tools/net10.0/any/".Length..]);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target);
                }
            }

            // What `dnx Farol.Mcp` runs, without installing it into the NuGet cache.
            await using var client = await McpClient.CreateAsync(
                new StdioClientTransport(new StdioClientTransportOptions
                {
                    Name = "farol",
                    Command = "dotnet",
                    Arguments = [Path.Combine(tool, "Farol.Host.dll"), "--root", TestPaths.ModernDirectory, "--autoload", "false"],
                }),
                cancellationToken: Ct);
            Assert.Equal(("farol", Version), (client.ServerInfo.Name, client.ServerInfo.Version));
            Assert.Equal(18, (await client.ListToolsAsync(cancellationToken: Ct)).Count);
            Assert.Equal(4, (await client.ListPromptsAsync(cancellationToken: Ct)).Count);
        }
        finally
        {
            try
            {
                output.Delete(recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The server process may hold its files for a moment after exiting; temp cleanup is best effort.
            }
        }
    }

    /// <summary>
    /// Tool and prompt names, annotations and parameters are a public contract (spec 001: breaking changes only in a
    /// major version). Descriptions are left out: rewording them breaks nobody.
    /// </summary>
    [Fact]
    public async Task The_mcp_surface_matches_its_snapshot()
    {
        await using var harness = await McpHarness.StartAsync(TestPaths.ModernDirectory, Ct);
        var tools = await harness.Client.ListToolsAsync(cancellationToken: Ct);
        var prompts = await harness.Client.ListPromptsAsync(cancellationToken: Ct);

        var surface = new JsonObject
        {
            ["tools"] = new JsonArray([.. tools.OrderBy(t => t.Name, StringComparer.Ordinal).Select(Describe)]),
            ["prompts"] = new JsonArray([.. prompts.OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => (JsonNode)new JsonObject
            {
                ["name"] = p.Name,
                ["arguments"] = new JsonArray([.. (p.ProtocolPrompt.Arguments ?? []).Select(a => (JsonNode)$"{a.Name}{(a.Required == true ? "" : "?")}")]),
            })]),
        };
        var actual = surface.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";

        var snapshot = Path.Combine(TestPaths.RepoRoot, "tests", "Farol.Tools.Tests", "Snapshots", "mcp-surface.json");
        var expected = File.Exists(snapshot) ? (await File.ReadAllTextAsync(snapshot, Ct)).ReplaceLineEndings("\n") : null;
        if (expected != actual)
        {
            var received = Path.ChangeExtension(snapshot, ".received.json");
            Directory.CreateDirectory(Path.GetDirectoryName(snapshot)!);
            await File.WriteAllTextAsync(received, actual, new UTF8Encoding(false), Ct);
            Assert.Fail(
                $"The MCP surface changed; the new one is in {received}. If the change is intended, replace {Path.GetFileName(snapshot)} with it, " +
                "and bump the major version when a tool, prompt or parameter was removed or renamed, or a parameter became required.");
        }
    }

    private static JsonNode Describe(McpClientTool tool)
    {
        var schema = tool.JsonSchema;
        var required = schema.TryGetProperty("required", out var list) ? list.EnumerateArray().Select(r => r.GetString()).ToHashSet(StringComparer.Ordinal) : [];
        var annotations = tool.ProtocolTool.Annotations;
        return new JsonObject
        {
            ["name"] = tool.Name,
            ["readOnly"] = annotations?.ReadOnlyHint,
            ["destructive"] = annotations?.DestructiveHint,
            ["idempotent"] = annotations?.IdempotentHint,
            ["openWorld"] = annotations?.OpenWorldHint,
            ["parameters"] = new JsonArray([.. schema.GetProperty("properties").EnumerateObject().Select(p => (JsonNode)$"{p.Name}{(required.Contains(p.Name) ? "" : "?")}: {TypeOf(p.Value)}")]),
        };
    }

    private static string TypeOf(JsonElement property) => property.TryGetProperty("type", out var type)
        ? type.ValueKind == JsonValueKind.Array ? string.Join('|', type.EnumerateArray().Select(t => t.GetString())) : type.GetString() ?? "?"
        : "any";

    // The tests run from bin/<configuration>/net10.0/: pack the host built in the same configuration.
    private static string Configuration => new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Parent!.Name;

    [GeneratedRegex(@"Farol\.Mcp@(?<version>[0-9A-Za-z.\-]+)")]
    private static partial Regex DnxPin();
}
