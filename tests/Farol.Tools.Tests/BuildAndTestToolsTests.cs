using System.Text.RegularExpressions;
using Farol.Testing;
using Xunit;

namespace Farol.Tools.Tests;

/// <summary>dotnet_build through the protocol, on fresh copies: builds write bin/obj and restore packages.</summary>
public sealed class BuildToolTests
{
    [Fact]
    public async Task AC19_legacy_builds_with_visual_studio_msbuild_and_groups_issues_by_project()
    {
        await using var server = await LegacyServer.StartAsync();
        Assert.SkipUnless(server.Available, "Needs Windows with Visual Studio or Build Tools.");

        var clean = await server.CallAsync("dotnet_build", []);
        await server.EditAsync(["Legacy.Core", "Orders", "OrderCalculator.cs"], ("return order.Subtotal() * (1 + TaxRate());", "return \"total\";"));
        var broken = await server.CallAsync("dotnet_build", new() { ["project"] = "Legacy.Core" });

        Assert.Contains("build: succeeded · 0 error(s), 2 warning(s)", clean, StringComparison.Ordinal);
        Assert.True(Regex.IsMatch(clean, @"toolchain: MSBuild \d+\.\d+ \([^)]*Visual Studio[^)]*\) · Legacy\.sln · Debug"), clean);
        Assert.Contains("Legacy.Core:\n- Legacy.Core/Serialization/LegacySerializer.cs:29 · warning CS0168", clean, StringComparison.Ordinal);
        // The packages.config restore audits too; its warning belongs to the project, not to the solution restore ran in.
        Assert.Contains(
            "Legacy.Tests:\n- Legacy.Tests/Legacy.Tests.csproj · warning NU1903 · Package 'Newtonsoft.Json' 12.0.3 has a known high severity vulnerability",
            clean,
            StringComparison.Ordinal);
        Assert.Contains("build: failed (exit code 1) · 1 error(s)", broken, StringComparison.Ordinal);
        Assert.Contains("- Legacy.Core/Orders/OrderCalculator.cs:23 · error CS0029 · Cannot implicitly convert type 'string' to 'decimal'", broken, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AC20_modern_builds_with_dotnet_and_reports_a_multi_target_error_once()
    {
        var ct = TestContext.Current.CancellationToken;
        using var copy = FixtureCopy.Create(TestPaths.ModernDirectory);
        await FixtureRestore.EnsureRestoredAsync(copy.PathOf("Modern.slnx"), ct);
        await Edit(copy.PathOf("src", "Modern.Core", "Pricing", "PriceCalculator.cs"), "var subtotal = prices.Sum();", "var subtotal = prices.Sum() + \"x\";");
        await using var harness = await McpHarness.StartAsync(copy.Root, ct);
        var progress = new Collected();

        var result = await harness.Client.CallToolAsync("dotnet_build", new Dictionary<string, object?>(), progress, cancellationToken: ct);
        var text = string.Join('\n', result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(t => t.Text));
        var messages = await Eventually.MatchesAsync(() => Task.FromResult(progress.Messages), m => m.Count > 0, ct);

        Assert.NotEqual(true, result.IsError);
        Assert.Contains(messages, m => m.StartsWith("building Modern.slnx with dotnet build", StringComparison.Ordinal));
        Assert.Contains("toolchain: dotnet build (.NET SDK", text, StringComparison.Ordinal);
        Assert.Contains("errors (1):", text, StringComparison.Ordinal);
        const string Error = "- src/Modern.Core/Pricing/PriceCalculator.cs:19 · error CS0019 · Operator '*' cannot be applied to operands of type 'string' and 'decimal' · net10.0, net48";
        Assert.Single(Regex.Matches(text, Regex.Escape(Error)));
    }

    private sealed class Collected : IProgress<ModelContextProtocol.ProgressNotificationValue>
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _messages = new();

        public IReadOnlyList<string> Messages => [.. _messages];

        public void Report(ModelContextProtocol.ProgressNotificationValue value) => _messages.Enqueue(value.Message ?? string.Empty);
    }

    internal static async Task Edit(string path, string old, string replacement)
    {
        var content = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        Assert.Contains(old, content, StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, content.Replace(old, replacement, StringComparison.Ordinal), TestContext.Current.CancellationToken);
    }
}

/// <summary>dotnet_test through the protocol: affected-test selection, failures with user frames, both test hosts.</summary>
public sealed class TestToolTests
{
    [Fact]
    public async Task AC21_only_the_tests_reaching_a_symbol_run_and_failures_show_user_frames()
    {
        var ct = TestContext.Current.CancellationToken;
        using var copy = FixtureCopy.Create(TestPaths.ModernDirectory);
        await FixtureRestore.EnsureRestoredAsync(copy.PathOf("Modern.slnx"), ct);
        await BuildToolTests.Edit(copy.PathOf("src", "Modern.Core", "Pricing", "PriceCalculator.cs"), "(1 - discount)", "(1 + discount)");
        await using var harness = await McpHarness.StartAsync(copy.Root, ct);

        var (isError, text) = await harness.CallAsync("dotnet_test", new() { ["affectedBy"] = "PriceCalculator.Total" }, ct);

        Assert.False(isError, text);
        Assert.Contains("selection: 3 of 4 test method(s) in Modern.Tests reach", text, StringComparison.Ordinal);
        Assert.Contains("tests: failed · 1 failed, 3 passed, 0 skipped · 4 run in 1 project(s)", text, StringComparison.Ordinal);
        Assert.Contains("Modern.Tests · dotnet test (Microsoft.Testing.Platform)", text, StringComparison.Ordinal);
        Assert.Contains("- failed Modern.Tests.PriceCalculatorTests.Total_applies_the_weekend_discount\n  Assert.Equal() Failure: Values differ", text, StringComparison.Ordinal);
        Assert.Contains("  at Modern.Tests.PriceCalculatorTests.Total_applies_the_weekend_discount() · tests/Modern.Tests/PriceCalculatorTests.cs:16", text, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Reflection", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SystemClock", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Classic_mstest_tests_run_with_vstest_console()
    {
        await using var server = await LegacyServer.StartAsync();
        Assert.SkipUnless(server.Available, "Needs Windows with Visual Studio or Build Tools.");

        var text = await server.CallAsync("dotnet_test", new() { ["affectedBy"] = "OrderCalculator.GetTotal" });

        Assert.Contains("selection: 2 of 3 test method(s) in Legacy.Tests reach", text, StringComparison.Ordinal);
        Assert.Contains("tests: passed · 0 failed, 2 passed, 0 skipped · 2 run in 1 project(s)", text, StringComparison.Ordinal);
        Assert.Matches(@"Legacy\.Tests · vstest\.console \(", text);
    }

    [Fact]
    public async Task AC22_build_and_test_are_refused_in_read_only_mode()
    {
        var ct = TestContext.Current.CancellationToken;
        using var copy = FixtureCopy.Create(TestPaths.ModernDirectory);
        await using var harness = await McpHarness.StartAsync(copy.Root, ct, o => o.ReadOnly = true);

        var build = await harness.CallAsync("dotnet_build", [], ct);
        var test = await harness.CallAsync("dotnet_test", [], ct);

        Assert.True(build.IsError);
        Assert.Contains("Refused to build: Farol was started with --read-only", build.Text, StringComparison.Ordinal);
        Assert.True(test.IsError);
        Assert.Contains("Refused to run tests: Farol was started with --read-only", test.Text, StringComparison.Ordinal);
    }
}
