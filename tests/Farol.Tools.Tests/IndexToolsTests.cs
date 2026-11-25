using Farol.Testing;
using Xunit;

namespace Farol.Tools.Tests;

/// <summary>Spec 002, AC-50 – AC-51: a server that starts on a workspace it indexed before answers navigation while the load runs.</summary>
public sealed class IndexToolsTests
{
    private const string Calculator = "src/Modern.Core/Pricing/PriceCalculator.cs";

    [Fact]
    public async Task AC50_AC51_navigation_and_overview_answer_from_the_index_while_the_load_runs_and_from_the_load_after()
    {
        var ct = TestContext.Current.CancellationToken;
        using var copy = FixtureCopy.Create(TestPaths.ModernDirectory);
        await FixtureRestore.EnsureRestoredAsync(copy.PathOf("Modern.slnx"), ct);
        var indexes = Directory.CreateTempSubdirectory("farol-index-").FullName;
        try
        {
            // A first server loads the workspace and writes its index after the warm-up.
            await using (var first = await McpHarness.StartAsync(copy.Root, ct, o => Indexed(o, indexes)))
            {
                await first.CallAsync("dotnet_workspace", new() { ["action"] = "load" }, ct);
                var status = await Eventually.MatchesAsync(
                    () => first.CallAsync("dotnet_workspace", [], ct), r => r.Text.Contains("index: written", StringComparison.Ordinal), ct, timeoutMs: 120_000);
                Has(status.Text, "index: written");
            }

            var file = copy.PathOf("src", "Modern.Core", "Pricing", "PriceCalculator.cs");
            await File.AppendAllTextAsync(file, "\n// changed after the index\n", ct);

            // The next server answers before its load is done: the hold keeps it loading.
            using var hold = LoadHold.Create();
            await using var second = await McpHarness.StartAsync(copy.Root, ct, o =>
            {
                Indexed(o, indexes);
                o.MSBuildProperties.Add(hold.Property);
            });
            var loading = await second.CallAsync("dotnet_workspace", [], ct);
            await hold.WaitUntilHeldAsync(ct);

            var symbols = await second.CallAsync("dotnet_find_symbols", new() { ["query"] = "PriceCalc" }, ct);
            var references = await second.CallAsync("dotnet_find_references", new() { ["symbol"] = "SystemClock.UtcNow" }, ct);
            var outline = await second.CallAsync("dotnet_outline", new() { ["path"] = Calculator }, ct);
            var symbol = await second.CallAsync("dotnet_symbol", new() { ["symbol"] = "SystemClock", ["include"] = "signature,docs,source" }, ct);
            var hierarchy = await second.CallAsync("dotnet_hierarchy", new() { ["symbol"] = "IClock" }, ct);
            var callers = await second.CallAsync("dotnet_call_hierarchy", new() { ["symbol"] = "PriceCalculator.IsWeekend" }, ct);
            var overview = await second.CallAsync("dotnet_overview", [], ct);
            var stillLoading = await second.CallAsync("dotnet_workspace", [], ct);

            foreach (var (name, answer) in new[] { ("symbols", symbols), ("references", references), ("outline", outline), ("symbol", symbol), ("hierarchy", hierarchy), ("callers", callers), ("overview", overview) })
            {
                Assert.False(answer.IsError, $"{name}: {answer.Text}");
                Assert.StartsWith("from the index written ", answer.Text, StringComparison.Ordinal);
                Has(answer.Text, $"changed since the index: {Calculator}; results in them are marked");
            }

            Has(symbols.Text, "class PriceCalculator");
            Has(symbols.Text, $"{Calculator}:13");
            var lines = symbols.Text.Split('\n');
            Assert.EndsWith(" · changed since the index", lines.Single(l => l.StartsWith("- class PriceCalculator ·", StringComparison.Ordinal)), StringComparison.Ordinal);
            Assert.DoesNotContain("changed since the index", lines.Single(l => l.StartsWith("- class PriceCalculatorTests ·", StringComparison.Ordinal)), StringComparison.Ordinal);
            Has(references.Text, "1 of them through property DateTime IClock.UtcNow { get; }");
            Has(references.Text, "clock.UtcNow");
            Has(outline.Text, "- 15 method decimal Total(IEnumerable<decimal> prices, decimal weekendDiscount)");
            Has(symbol.Text, "inherits: IClock");
            Has(hierarchy.Text, "implementation · class SystemClock");
            Has(callers.Text, "PriceCalculator.Total(");
            Has(overview.Text, "Modern.Core");
            Has(stillLoading.Text, "hint: navigation and dotnet_overview answer now from the index written");
            Has(loading.Text, "state: loading");

            // Once the load is done, the answers come from it.
            hold.Release();
            await second.CallAsync("dotnet_workspace", new() { ["action"] = "load" }, ct);
            var loaded = await second.CallAsync("dotnet_find_symbols", new() { ["query"] = "PriceCalc" }, ct);
            Assert.DoesNotContain("from the index", loaded.Text, StringComparison.Ordinal);
            Has(loaded.Text, "class PriceCalculator");
        }
        finally
        {
            try
            {
                Directory.Delete(indexes, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static void Has(string text, string expected) => Assert.True(text.Contains(expected, StringComparison.Ordinal), $"'{expected}' not in:\n{text}");

    private static void Indexed(Farol.Engine.FarolEngineOptions options, string directory)
    {
        options.Index = true;
        options.IndexDirectory = directory;
    }
}
