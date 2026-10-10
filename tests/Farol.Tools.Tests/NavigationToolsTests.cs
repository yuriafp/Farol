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

        Assert.Contains("in 5 project(s)", text, StringComparison.Ordinal);
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
    public async Task A_source_position_outside_the_root_is_refused()
    {
        Assert.SkipUnless(fixture.Available, "Needs Windows with Visual Studio or Build Tools.");

        var (isError, text) = await fixture.Harness!.CallAsync("dotnet_symbol", new() { ["symbol"] = "../elsewhere/Secret.cs:3" }, TestContext.Current.CancellationToken);

        Assert.True(isError);
        Assert.Contains("Path '../elsewhere/Secret.cs' resolves outside the directories Farol trusts", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dotnet_find_symbols", "query")]
    [InlineData("dotnet_symbol", "symbol")]
    [InlineData("dotnet_find_references", "symbol")]
    [InlineData("dotnet_code_actions", "path")]
    public async Task An_empty_argument_is_an_invalid_argument_with_a_hint(string tool, string parameter)
    {
        Assert.SkipUnless(fixture.Available, "Needs Windows with Visual Studio or Build Tools.");
        var arguments = new Dictionary<string, object?> { [parameter] = " " };
        if (tool == "dotnet_code_actions")
        {
            arguments["line"] = 1;
        }

        var (isError, text) = await fixture.Harness!.CallAsync(tool, arguments, TestContext.Current.CancellationToken);

        Assert.True(isError);
        Assert.Contains($"'{parameter}' is empty. Pass ", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Find_symbols_in_an_unknown_project_lists_the_projects()
    {
        Assert.SkipUnless(fixture.Available, "Needs Windows with Visual Studio or Build Tools.");

        var (isError, text) = await fixture.Harness!.CallAsync(
            "dotnet_find_symbols", new() { ["query"] = "OrderCalculator", ["project"] = "Legacy.Nope" }, TestContext.Current.CancellationToken);

        Assert.True(isError);
        Assert.Contains("No project named 'Legacy.Nope'. Projects: Legacy.Core, Legacy.Desktop", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AC43_dotnet_symbol_finds_a_framework_type_by_name()
    {
        var text = await CallAsync("dotnet_symbol", new() { ["symbol"] = "StringBuilder" });

        Assert.Contains("T:System.Text.StringBuilder", text, StringComparison.Ordinal);
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

    [Fact]
    public async Task AC43_framework_types_resolve_by_name_but_only_source_has_an_outline_or_implementations()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await McpHarness.StartAsync(fixture.Copy.Root, ct);

        var member = await harness.CallAsync("dotnet_symbol", new() { ["symbol"] = "StringBuilder.Capacity" }, ct);
        var outline = await harness.CallAsync("dotnet_outline", new() { ["symbol"] = "StringBuilder" }, ct);
        var disposable = await harness.CallAsync("dotnet_hierarchy", new() { ["symbol"] = "IDisposable", ["direction"] = "implementations" }, ct);
        var clock = await harness.CallAsync("dotnet_hierarchy", new() { ["symbol"] = "IClock", ["direction"] = "implementations" }, ct);

        Assert.False(member.IsError, member.Text);
        Assert.Contains("P:System.Text.StringBuilder.Capacity", member.Text, StringComparison.Ordinal);
        Assert.True(outline.IsError);
        Assert.Contains("'StringBuilder' is class ", outline.Text, StringComparison.Ordinal);
        Assert.Contains("which has no source in the workspace", outline.Text, StringComparison.Ordinal);
        Assert.False(disposable.IsError, disposable.Text);
        Assert.Contains("nothing found in that direction.", disposable.Text, StringComparison.Ordinal);
        Assert.Contains("implementation · class SystemClock", clock.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_path_that_is_no_source_file_says_why_without_an_absolute_path()
    {
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(fixture.Copy.PathOf("Loose.cs"), "class Loose { }", ct);
        await using var harness = await McpHarness.StartAsync(fixture.Copy.Root, ct);
        var reasons = new Dictionary<string, string>
        {
            ["src/Modern.Core/Pricing"] = "It is a folder: pass one of its .cs or .vb files.",
            ["src/Modern.Core/Modern.Core.csproj"] = "Only C# and VB files (.cs, .vb) are source files.",
            ["src/Modern.Core/Pricing/Missing.cs"] = "No file has that path, relative to the root Farol runs in or to the workspace's folder",
            ["Loose.cs"] = "It is not inside the folder of any project of the workspace.",
        };

        foreach (var (path, reason) in reasons)
        {
            var (isError, text) = await harness.CallAsync("dotnet_outline", new() { ["path"] = path }, ct);

            Assert.True(isError, path);
            Assert.Contains($"'{path}' is not a source file of the workspace. {reason}", text, StringComparison.Ordinal);
            Assert.DoesNotContain(fixture.Copy.Root.Replace('\\', '/'), text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
