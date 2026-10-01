using Xunit;

namespace Farol.Tools.Tests;

/// <summary>Navigation tools through the MCP protocol, on a copy of the legacy fixture (C# and VB.NET).</summary>
public sealed class NavigationToolsTests(LegacyMcpFixture fixture) : IClassFixture<LegacyMcpFixture>
{
    [Fact]
    public async Task AC5_find_symbols_returns_location_and_id()
    {
        var text = await CallAsync("dotnet_find_symbols", new() { ["query"] = "OrdCalc" });

        Assert.Contains("class OrderCalculator · Legacy.Core/Orders/OrderCalculator.cs:6", text, StringComparison.Ordinal);
        Assert.Contains("id T:Legacy.Core.Orders.OrderCalculator", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AC6_find_references_groups_csharp_and_vb_call_sites_by_project()
    {
        var text = await CallAsync("dotnet_find_references", new() { ["symbol"] = "OrderCalculator.GetTotal" });

        Assert.Contains("in 4 project(s)", text, StringComparison.Ordinal);
        Assert.Contains("defined at: Legacy.Core/Orders/OrderCalculator.cs:15", text, StringComparison.Ordinal);
        Assert.Contains("Legacy.VbLib:", text, StringComparison.Ordinal);
        Assert.Contains("Legacy.VbLib/ShippingCalculator.vb:", text, StringComparison.Ordinal);
        Assert.Contains("· call ·", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AC9_ambiguous_symbol_lists_candidates_without_an_error()
    {
        var text = await CallAsync("dotnet_symbol", new() { ["symbol"] = "GetTotal" });

        Assert.Contains("'GetTotal' matches 3 symbols", text, StringComparison.Ordinal);
        Assert.Contains("id M:Legacy.Web.IOrderService.GetTotal(System.Int32)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Symbol_source_of_a_vb_member_is_the_whole_block()
    {
        var text = await CallAsync("dotnet_symbol", new() { ["symbol"] = "ShippingCalculator.TotalWithShipping", ["include"] = "signature,source" });

        Assert.Contains("```vb", text, StringComparison.Ordinal);
        Assert.Contains("Public Function TotalWithShipping", text, StringComparison.Ordinal);
        Assert.Contains("End Function", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AC10_hierarchy_lists_interface_implementations()
    {
        var text = await CallAsync("dotnet_hierarchy", new() { ["symbol"] = "IOrderRepository", ["direction"] = "implementations" });

        Assert.Contains("implementation · class InMemoryOrderRepository", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AC11_call_hierarchy_lists_callers_with_call_sites()
    {
        var text = await CallAsync("dotnet_call_hierarchy", new() { ["symbol"] = "OrderCalculator.GetTotal" });

        Assert.Contains("ShippingCalculator.TotalWithShipping", text, StringComparison.Ordinal);
        Assert.Contains("_Default.btnCalculate_Click", text, StringComparison.Ordinal);
        Assert.Contains("· at Legacy.Web/Default.aspx.cs:", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AC12_outline_lists_members_without_bodies()
    {
        var text = await CallAsync("dotnet_outline", new() { ["path"] = "Legacy.Core/Orders/OrderCalculator.cs" });

        Assert.Contains("- 6 class OrderCalculator", text, StringComparison.Ordinal);
        Assert.Contains("  - 15 method decimal GetTotal(int orderId)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("return", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AC7_find_references_includes_the_aspx_event_wiring()
    {
        var text = await CallAsync("dotnet_find_references", new() { ["symbol"] = "_Default.btnCalculate_Click" });

        Assert.Contains("Legacy.Web/Default.aspx:12 · markup ·", text, StringComparison.Ordinal);
        Assert.Contains("OnClick=\"btnCalculate_Click\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AC8_find_references_includes_the_xaml_event_wiring()
    {
        var text = await CallAsync("dotnet_find_references", new() { ["symbol"] = "MainWindow.OnCalculateClick" });

        Assert.Contains("Legacy.Wpf/MainWindow.xaml:10 · markup ·", text, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnCalculateClick\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Markup_kind_filter_returns_only_markup_references()
    {
        var text = await CallAsync("dotnet_find_references", new() { ["symbol"] = "_Default.lblTotal", ["kinds"] = "markup" });

        Assert.Contains("Legacy.Web/Default.aspx:11 · markup ·", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Default.aspx.cs", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_symbol_is_an_actionable_error()
    {
        Assert.SkipUnless(fixture.Available, "Needs Windows with Visual Studio or Build Tools.");

        var (isError, text) = await fixture.Harness!.CallAsync("dotnet_symbol", new() { ["symbol"] = "NoSuchThing" }, TestContext.Current.CancellationToken);

        Assert.True(isError);
        Assert.Contains("dotnet_find_symbols", text, StringComparison.Ordinal);
    }

    private async Task<string> CallAsync(string tool, Dictionary<string, object?> arguments)
    {
        Assert.SkipUnless(fixture.Available, "Needs Windows with Visual Studio or Build Tools.");
        var (isError, text) = await fixture.Harness!.CallAsync(tool, arguments, TestContext.Current.CancellationToken);
        Assert.False(isError, text);
        return text;
    }
}

/// <summary>AC-13 through the protocol: one hit per position, listing every target framework it was found in.</summary>
public sealed class MultiTargetNavigationToolsTests(ModernCopyFixture fixture) : IClassFixture<ModernCopyFixture>
{
    [Fact]
    public async Task AC13_references_list_the_target_frameworks_they_occur_in()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await McpHarness.StartAsync(fixture.Copy.Root, ct);

        var (isError, text) = await harness.CallAsync("dotnet_find_references", new() { ["symbol"] = "IClock" }, ct);

        Assert.False(isError, text);
        Assert.Contains("net10.0, net48", text, StringComparison.Ordinal);
        Assert.Contains("src/Modern.Api/Program.cs:", text, StringComparison.Ordinal);
    }
}
