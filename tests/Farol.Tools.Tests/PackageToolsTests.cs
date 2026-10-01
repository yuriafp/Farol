using Farol.Testing;
using Xunit;

namespace Farol.Tools.Tests;

/// <summary>
/// dotnet_packages and dotnet_package_api through the protocol, against nuget.org. The fixtures pin packages with
/// published advisories and deprecations on purpose (see tests/fixtures/README.md).
/// </summary>
public sealed class PackageToolsTests(ModernCopyFixture modern) : IClassFixture<ModernCopyFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AC23_modern_packages_show_resolved_versions_chains_and_flags()
    {
        await using var harness = await McpHarness.StartAsync(modern.Copy.Root, Ct);

        var (isError, text) = await harness.CallAsync("dotnet_packages", [], Ct);

        Assert.False(isError, text);
        Assert.Contains("packages: Modern.slnx · 3 of 3 project(s) use packages · ", text, StringComparison.Ordinal);
        Assert.Contains(": 3 vulnerable, 7 deprecated,", text, StringComparison.Ordinal);
        Assert.Contains("Modern.Api (net10.0) · versions in Directory.Packages.props · 1 direct, 7 transitive", text, StringComparison.Ordinal);
        Assert.Matches(@"- Microsoft\.AspNetCore\.Authentication\.JwtBearer 8\.0\.0 · latest \d+\.\d+\.\d+", text);
        Assert.Contains(
            "- transitive System.IdentityModel.Tokens.Jwt 7.0.3 · vulnerable: moderate GHSA-59j7-ghrg-fj52 · deprecated (legacy) · " +
            "via Microsoft.AspNetCore.Authentication.JwtBearer > Microsoft.IdentityModel.Protocols.OpenIdConnect",
            text,
            StringComparison.Ordinal);
        Assert.Contains("- transitive Microsoft.IdentityModel.Protocols.OpenIdConnect 7.0.3 · deprecated (legacy, critical bugs) · via Microsoft.AspNetCore.Authentication.JwtBearer", text, StringComparison.Ordinal);
        Assert.Contains("Modern.Core (net10.0, net48) · versions in Directory.Packages.props · 1 direct, 8 transitive", text, StringComparison.Ordinal);
        Assert.Matches(@"- System\.Text\.Json 8\.0\.4 · net48 · vulnerable: high GHSA-8g4q-xg66-9fp4 · latest \d+", text);
        Assert.Contains("  other transitive (8): Microsoft.Bcl.AsyncInterfaces 8.0.0 (net48),", text, StringComparison.Ordinal);
        Assert.Contains("- xunit.v3 4.0.1", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Problem_filters_list_only_the_matching_packages()
    {
        await using var harness = await McpHarness.StartAsync(modern.Copy.Root, Ct);

        var (_, vulnerable) = await harness.CallAsync("dotnet_packages", new() { ["include"] = "vulnerable" }, Ct);
        var (_, outdated) = await harness.CallAsync("dotnet_packages", new() { ["project"] = "Modern.Core", ["include"] = "direct,outdated" }, Ct);
        var (isError, wrong) = await harness.CallAsync("dotnet_packages", new() { ["include"] = "risky" }, Ct);

        Assert.Equal(3, vulnerable.Split('\n').Count(l => l.StartsWith("- ", StringComparison.Ordinal)));
        Assert.DoesNotContain("xunit", vulnerable, StringComparison.Ordinal);
        Assert.DoesNotContain("Modern.Tests", vulnerable, StringComparison.Ordinal);
        Assert.Single(outdated.Split('\n'), l => l.StartsWith("- ", StringComparison.Ordinal));
        Assert.Contains("- System.Text.Json 8.0.4", outdated, StringComparison.Ordinal);
        Assert.True(isError);
        Assert.Contains("Unknown include option 'risky'.", wrong, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Offline_the_vulnerabilities_come_from_the_last_restore()
    {
        await using var harness = await McpHarness.StartAsync(modern.Copy.Root, Ct, o => o.Offline = true);

        var (isError, text) = await harness.CallAsync("dotnet_packages", [], Ct);

        Assert.False(isError, text);
        Assert.Contains("feeds: not checked (--offline)", text, StringComparison.Ordinal);
        Assert.Contains(": 3 vulnerable, 0 deprecated, 0 direct outdated", text, StringComparison.Ordinal);
        Assert.Contains("- System.Text.Json 8.0.4 · net48 · vulnerable: high GHSA-8g4q-xg66-9fp4", text, StringComparison.Ordinal);
        Assert.DoesNotContain("latest", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AC24_package_api_returns_the_signatures_and_docs_of_that_exact_version()
    {
        await using var harness = await McpHarness.StartAsync(modern.Copy.Root, Ct);
        var query = new Dictionary<string, object?> { ["package"] = "Newtonsoft.Json", ["query"] = "JsonConvert.SerializeObject", ["targetFramework"] = "netstandard2.0" };

        var (isError, v12) = await harness.CallAsync("dotnet_package_api", new(query) { ["version"] = "12.0.3" }, Ct);
        var (_, v13) = await harness.CallAsync("dotnet_package_api", new(query) { ["version"] = "13.0.3" }, Ct);
        var (_, type) = await harness.CallAsync("dotnet_package_api", new() { ["package"] = "Newtonsoft.Json", ["version"] = "12.0.3", ["query"] = "JsonConvert", ["maxTokens"] = 600 }, Ct);

        Assert.False(isError, v12);
        Assert.StartsWith("Newtonsoft.Json 12.0.3 · lib/netstandard2.0 (also: net20,", v12, StringComparison.Ordinal);
        Assert.Contains("'JsonConvert.SerializeObject' matches 8 public type(s) or member(s):", v12, StringComparison.Ordinal);
        Assert.Contains(
            "- method static string JsonConvert.SerializeObject(object? value, JsonSerializerSettings settings) — Serializes the specified object to a JSON string using JsonSerializerSettings.",
            v12,
            StringComparison.Ordinal);
        // 13.0 annotated the settings parameter as nullable: the answer is the API of the version asked for.
        Assert.Contains("- method static string JsonConvert.SerializeObject(object? value, JsonSerializerSettings? settings) —", v13, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonSerializerSettings settings)", v13, StringComparison.Ordinal);
        Assert.Contains("class Newtonsoft.Json.JsonConvert · static", type, StringComparison.Ordinal);
        Assert.Contains("summary: Provides methods for converting between .NET types and JSON types.", type, StringComparison.Ordinal);
        Assert.Contains("- field static readonly string True — Represents JavaScript's boolean value true as a string.", type, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AC24_offline_a_package_missing_from_the_caches_is_reported_not_guessed()
    {
        await using var harness = await McpHarness.StartAsync(modern.Copy.Root, Ct, o => o.Offline = true);

        var (isError, text) = await harness.CallAsync("dotnet_package_api", new() { ["package"] = "Newtonsoft.Json", ["version"] = "1.0.0-farol-missing", ["query"] = "JsonConvert" }, Ct);

        Assert.True(isError);
        Assert.Contains("Newtonsoft.Json 1.0.0-farol-missing is not in the local NuGet caches, and Farol runs with --offline, so it cannot download it.", text, StringComparison.Ordinal);
        Assert.Contains("Farol does not guess an API it cannot read.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_version_package_api_reads_the_one_the_workspace_resolves()
    {
        await using var harness = await McpHarness.StartAsync(modern.Copy.Root, Ct);

        var (isError, text) = await harness.CallAsync("dotnet_package_api", new() { ["package"] = "System.Text.Json", ["query"] = "JsonSerializer.SerializeToUtf8Bytes", ["maxTokens"] = 600 }, Ct);

        Assert.False(isError, text);
        Assert.StartsWith("System.Text.Json 8.0.4 · lib/net462 (also:", text, StringComparison.Ordinal);
        Assert.Contains("version: the one Modern.Core resolve(s)", text, StringComparison.Ordinal);
        Assert.Contains("static byte[] JsonSerializer.SerializeToUtf8Bytes<TValue>(TValue value, JsonSerializerOptions? options = null)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_issues_show_restore_warnings_as_warnings_with_relative_paths()
    {
        await using var harness = await McpHarness.StartAsync(modern.Copy.Root, Ct);

        var (_, text) = await harness.CallAsync("dotnet_workspace", new() { ["action"] = "load" }, Ct);

        Assert.Contains("- warning: NU1903 in src/Modern.Core/Modern.Core.csproj: ", text, StringComparison.Ordinal);
        Assert.Contains("- warning: NU1902 in src/Modern.Api/Modern.Api.csproj: ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("error:", text, StringComparison.Ordinal);
        Assert.DoesNotContain(modern.Copy.Root, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task External_symbols_name_their_package_or_framework_and_decompile()
    {
        await using var harness = await McpHarness.StartAsync(modern.Copy.Root, Ct);

        var (isError, text) = await harness.CallAsync("dotnet_symbol", new() { ["symbol"] = "M:System.Math.Round(System.Decimal,System.Int32)", ["include"] = "signature,source" }, Ct);
        var (_, type) = await harness.CallAsync("dotnet_symbol", new() { ["symbol"] = "T:System.Text.Json.JsonSerializer", ["include"] = "source", ["maxTokens"] = 6000 }, Ct);

        Assert.False(isError, text);
        Assert.StartsWith("method decimal Math.Round(decimal d, int decimals)", text, StringComparison.Ordinal);
        Assert.Contains("declared in: ", text, StringComparison.Ordinal);
        Assert.Contains("source: decompiled from ", text, StringComparison.Ordinal);
        Assert.Contains("decimal.Round(d, decimals)", text, StringComparison.Ordinal);
        Assert.DoesNotContain(":\\", text, StringComparison.Ordinal);
        Assert.Contains("skeleton (public API, from metadata):", type, StringComparison.Ordinal);
        Assert.Contains("- method static string Serialize<TValue>(TValue value, JsonSerializerOptions? options = null)", type, StringComparison.Ordinal);
    }
}

/// <summary>packages.config through the protocol: every package listed with its flags.</summary>
public sealed class PackagesConfigToolTests
{
    [Fact]
    public async Task AC23_packages_config_packages_are_listed_with_their_flags()
    {
        await using var server = await LegacyServer.StartAsync();
        Assert.SkipUnless(server.Available, "Needs Windows with Visual Studio or Build Tools.");

        var text = await server.CallAsync("dotnet_packages", []);

        Assert.Contains("packages: Legacy.sln · 1 of 6 project(s) use packages · 3 package version(s): 1 vulnerable, 2 deprecated,", text, StringComparison.Ordinal);
        Assert.Contains("Legacy.Tests (net48) · packages.config · 3 package(s)", text, StringComparison.Ordinal);
        Assert.Matches(@"- MSTest\.TestFramework 2\.2\.10 · deprecated \(legacy\) · latest \d+", text);
        Assert.Matches(@"- Newtonsoft\.Json 12\.0\.3 · vulnerable: high GHSA-5crp-9r3c-p9vr · latest \d+", text);
        Assert.Contains("no packages: Legacy.Core, Legacy.Desktop, Legacy.VbLib, Legacy.Web, Legacy.Wpf", text, StringComparison.Ordinal);
    }
}
