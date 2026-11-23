using Farol.Testing;
using Xunit;

namespace Farol.Tools.Tests;

/// <summary>Spec 002, AC-58 – AC-59: project analyzers in dotnet_check, opt-in per call or for the server (the hook's included).</summary>
public sealed class AnalyzerCheckTests
{
    private const string Calculator = "src/Modern.Core/Pricing/PriceCalculator.cs";

    // Modern.Core's .editorconfig raises CA1507 (use nameof) to an error; this guard breaks it.
    private const string Guard = "    public static void Guard(string value)\n    {\n        if (value is null) throw new ArgumentNullException(\"value\");\n    }\n\n    private static bool IsWeekend";

    private static async Task EditAsync(FixtureCopy copy, CancellationToken ct)
    {
        var file = copy.PathOf("src", "Modern.Core", "Pricing", "PriceCalculator.cs");
        await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file, ct)).Replace("    private static bool IsWeekend", Guard, StringComparison.Ordinal), ct);
    }

    [Fact]
    public async Task AC58_an_analyzer_error_is_new_with_analyzers_and_absent_without()
    {
        var ct = TestContext.Current.CancellationToken;
        using var copy = FixtureCopy.Create(TestPaths.ModernDirectory);
        await FixtureRestore.EnsureRestoredAsync(copy.PathOf("Modern.slnx"), ct);
        await using var harness = await McpHarness.StartAsync(copy.Root, ct);
        await harness.CallAsync("dotnet_workspace", new() { ["action"] = "load" }, ct);

        await EditAsync(copy, ct);
        var without = await Eventually.MatchesAsync(() => harness.CallAsync("dotnet_check", [], ct), r => r.Text.Contains("changed: 1 file(s)", StringComparison.Ordinal), ct);
        var with = await harness.CallAsync("dotnet_check", new() { ["analyzers"] = true }, ct);

        Assert.Contains("new since load: none", without.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("analyzers:", without.Text, StringComparison.Ordinal);
        Assert.Contains("error CA1507", with.Text, StringComparison.Ordinal);
        Assert.Contains($"{Calculator}:", with.Text, StringComparison.Ordinal);
        Assert.Matches(@"analyzers: \d+ on \d+ file\(s\) in \d+\.\d s", with.Text);
    }

    [Fact]
    public async Task AC58_with_check_analyzers_on_the_hook_blocks_on_the_analyzer_error()
    {
        var ct = TestContext.Current.CancellationToken;
        using var copy = FixtureCopy.Create(TestPaths.ModernDirectory);
        await FixtureRestore.EnsureRestoredAsync(copy.PathOf("Modern.slnx"), ct);
        await using var harness = await McpHarness.StartAsync(copy.Root, ct, o => o.CheckAnalyzers = true);
        await harness.CallAsync("dotnet_workspace", new() { ["action"] = "load" }, ct);

        await EditAsync(copy, ct);
        var hook = await harness.CallAsync("dotnet_check", new() { ["format"] = "hook", ["edited"] = Calculator }, ct);
        var optedOut = await harness.CallAsync("dotnet_check", new() { ["analyzers"] = false }, ct);

        Assert.Contains("\"decision\":\"block\"", hook.Text, StringComparison.Ordinal);
        Assert.Contains("CA1507", hook.Text, StringComparison.Ordinal);
        Assert.Contains("new since load: none", optedOut.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Analyzer_diagnostics_that_existed_at_load_are_not_new()
    {
        var ct = TestContext.Current.CancellationToken;
        using var copy = FixtureCopy.Create(TestPaths.ModernDirectory);
        await EditAsync(copy, ct); // before the load: the violation is part of the baseline
        await FixtureRestore.EnsureRestoredAsync(copy.PathOf("Modern.slnx"), ct);
        await using var harness = await McpHarness.StartAsync(copy.Root, ct);
        await harness.CallAsync("dotnet_workspace", new() { ["action"] = "load" }, ct);

        var file = copy.PathOf("src", "Modern.Core", "Pricing", "PriceCalculator.cs");
        await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file, ct)).Replace("var subtotal = prices.Sum();", "var subtotal = prices.Sum() + 0m;", StringComparison.Ordinal), ct);
        var text = await Eventually.MatchesAsync(() => harness.CallAsync("dotnet_check", new() { ["analyzers"] = true, ["includeExisting"] = true }, ct), r => r.Text.Contains("changed: 1 file(s)", StringComparison.Ordinal), ct);

        Assert.Contains("new since load: none", text.Text, StringComparison.Ordinal);
        Assert.Contains("CA1507", text.Text, StringComparison.Ordinal);
    }
}
