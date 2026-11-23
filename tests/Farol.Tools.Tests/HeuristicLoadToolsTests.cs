using Xunit;

namespace Farol.Tools.Tests;

/// <summary>Spec 002, AC-54 – AC-55: the tools over a heuristic load of the legacy fixture, on any OS.</summary>
public sealed class HeuristicLoadToolsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AC54_overview_says_the_load_is_heuristic_and_references_cross_languages()
    {
        await using var server = await LegacyServer.StartHeuristicAsync();

        var overview = await server.CallAsync("dotnet_overview", []);
        var references = await server.CallAsync("dotnet_find_references", new() { ["symbol"] = "OrderCalculator.GetTotal" });
        var status = await server.CallAsync("dotnet_workspace", []);

        Assert.Contains("load: heuristic", overview, StringComparison.Ordinal);
        Assert.Contains("6 project(s)", overview, StringComparison.Ordinal);
        Assert.Contains("loader: heuristic", status, StringComparison.Ordinal);
        foreach (var project in new[] { "Legacy.Desktop", "Legacy.Tests", "Legacy.VbLib", "Legacy.Web", "Legacy.Wpf" })
        {
            Assert.Contains(project, references, StringComparison.Ordinal);
        }

        Assert.Contains(".vb:", references, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AC55_check_reports_the_new_error_and_says_the_load_is_heuristic_while_build_and_test_need_msbuild()
    {
        await using var server = await LegacyServer.StartHeuristicAsync();

        await server.EditAsync(["Legacy.Core", "Orders", "OrderCalculator.cs"], ("public decimal GetTotal(int orderId)", "public int Broken() => \"text\";\n\n        public decimal GetTotal(int orderId)"));
        var check = await server.CallUntilAsync("dotnet_check", [], t => t.Contains("CS0029", StringComparison.Ordinal));
        var (buildFailed, build) = await server.Harness!.CallAsync("dotnet_build", [], Ct);
        var (testFailed, test) = await server.Harness!.CallAsync("dotnet_test", new() { ["project"] = "Legacy.Tests" }, Ct);

        Assert.Contains("new since load: 1 error", check, StringComparison.Ordinal);
        Assert.Contains("heuristic load", check, StringComparison.Ordinal);
        Assert.DoesNotContain("InitializeComponent", check, StringComparison.Ordinal); // the WPF project's load errors are baseline
        if (build.Contains("toolchain: MSBuild", StringComparison.Ordinal))
        {
            // A heuristic load forced where Visual Studio is installed: its MSBuild builds, and finds the same error.
            Assert.Contains("build: failed", build, StringComparison.Ordinal);
            Assert.Contains("CS0029", build, StringComparison.Ordinal);
            return;
        }

        Assert.True(buildFailed, build);
        Assert.True(testFailed, test);
        Assert.Contains("needs the MSBuild of Visual Studio or Build Tools", build, StringComparison.Ordinal);
        Assert.Contains("needs the MSBuild of Visual Studio or Build Tools", test, StringComparison.Ordinal);
    }
}
