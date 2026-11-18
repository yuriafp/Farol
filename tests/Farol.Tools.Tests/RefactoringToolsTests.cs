using Farol.Testing;
using Xunit;

namespace Farol.Tools.Tests;

/// <summary>Spec 002, AC-60, AC-61 and AC-65: dotnet_rename on the legacy fixture (C#, VB, WebForms, WPF, classic projects).</summary>
public sealed class RefactoringToolsTests
{
    private const string Calculator = "Legacy.Core/Orders/OrderCalculator.cs";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void Has(string text, string expected) => Assert.True(text.Contains(expected, StringComparison.Ordinal), $"Missing: {expected}\n{text}");

    [Fact]
    public async Task AC60_rename_shows_a_diff_of_the_csharp_and_vb_references_and_writes_nothing()
    {
        await using var server = await LegacyServer.StartAsync();
        Assert.SkipUnless(server.Available, "Needs Windows with Visual Studio Build Tools to load the legacy fixture.");
        var before = await File.ReadAllTextAsync(server.Copy.PathOf("Legacy.VbLib", "ShippingCalculator.vb"), Ct);

        var text = await server.CallAsync("dotnet_rename", new() { ["symbol"] = "OrderCalculator.GetTotal", ["newName"] = "CalculateTotal" });

        Has(text, "preview of rename decimal OrderCalculator.GetTotal(int orderId) → CalculateTotal");
        Has(text, "+        public decimal CalculateTotal(int orderId)");
        Has(text, "_calculator.CalculateTotal(id)");
        foreach (var file in new[] { "Legacy.Desktop/MainForm.cs", "Legacy.VbLib/ShippingCalculator.vb", "Legacy.Web/Default.aspx.cs", "Legacy.Wpf/MainWindow.xaml.cs", "Legacy.Tests/OrderCalculatorTests.cs" })
        {
            Has(text, $"+++ b/{file}");
        }

        // IOrderService.GetTotal is another method with the same name: it keeps it.
        Assert.DoesNotContain("+++ b/Legacy.Web/IOrderService.cs", text, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllTextAsync(server.Copy.PathOf("Legacy.VbLib", "ShippingCalculator.vb"), Ct));
    }

    [Fact]
    public async Task AC60_rename_with_apply_writes_the_files_and_the_check_is_clean()
    {
        await using var server = await LegacyServer.StartAsync();
        Assert.SkipUnless(server.Available, "Needs Windows with Visual Studio Build Tools to load the legacy fixture.");

        var text = await server.CallAsync("dotnet_rename", new() { ["symbol"] = "OrderCalculator.GetTotal", ["newName"] = "CalculateTotal", ["apply"] = true });

        Has(text, "applied rename");
        Has(text, "new since load: none");
        Assert.Contains("_calculator.CalculateTotal(id)", await File.ReadAllTextAsync(server.Copy.PathOf("Legacy.VbLib", "ShippingCalculator.vb"), Ct), StringComparison.Ordinal);
        Assert.Contains("public decimal CalculateTotal(int orderId)", await File.ReadAllTextAsync(server.Copy.PathOf("Legacy.Core", "Orders", "OrderCalculator.cs"), Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AC60_rename_changes_webforms_and_xaml_markup_references()
    {
        await using var server = await LegacyServer.StartAsync();
        Assert.SkipUnless(server.Available, "Needs Windows with Visual Studio Build Tools to load the legacy fixture.");

        var web = await server.CallAsync("dotnet_rename", new() { ["symbol"] = "_Default.btnCalculate_Click", ["newName"] = "OnCalculate", ["apply"] = true });
        var wpf = await server.CallAsync("dotnet_rename", new() { ["symbol"] = "MainWindow.OnCalculateClick", ["newName"] = "OnComputeClick" });

        Has(web, "+++ b/Legacy.Web/Default.aspx");
        Has(web, "new since load: none");
        Assert.Contains("OnClick=\"OnCalculate\"", await File.ReadAllTextAsync(server.Copy.PathOf("Legacy.Web", "Default.aspx"), Ct), StringComparison.Ordinal);
        Assert.Contains("+        <Button Content=\"Calculate\" Click=\"OnComputeClick\" />", wpf, StringComparison.Ordinal);
        Has(wpf, "+++ b/Legacy.Wpf/MainWindow.xaml.cs");
    }

    [Fact]
    public async Task AC61_renaming_a_type_renames_its_file_and_project_item()
    {
        await using var server = await LegacyServer.StartAsync();
        Assert.SkipUnless(server.Available, "Needs Windows with Visual Studio Build Tools to load the legacy fixture.");

        var text = await server.CallAsync("dotnet_rename", new() { ["symbol"] = "OrderCalculator", ["newName"] = "TotalCalculator", ["apply"] = true });

        Has(text, "renames Legacy.Core/Orders/OrderCalculator.cs → Legacy.Core/Orders/TotalCalculator.cs");
        Has(text, "new since load: none");
        Assert.False(File.Exists(server.Copy.PathOf("Legacy.Core", "Orders", "OrderCalculator.cs")));
        Assert.Contains("public class TotalCalculator", await File.ReadAllTextAsync(server.Copy.PathOf("Legacy.Core", "Orders", "TotalCalculator.cs"), Ct), StringComparison.Ordinal);
        Assert.Contains("<Compile Include=\"Orders\\TotalCalculator.cs\" />", await File.ReadAllTextAsync(server.Copy.PathOf("Legacy.Core", "Legacy.Core.csproj"), Ct), StringComparison.Ordinal);

        // The project-file change Farol wrote does not reload the workspace: the baseline still holds.
        var check = await server.CallAsync("dotnet_check", []);
        Has(check, "new since load: none");
    }

    [Fact]
    public async Task AC61_a_moved_file_keeps_its_load_diagnostics()
    {
        await using var server = await LegacyServer.StartAsync();
        Assert.SkipUnless(server.Available, "Needs Windows with Visual Studio Build Tools to load the legacy fixture.");
        var before = await server.CallAsync("dotnet_check", new() { ["scope"] = "project", ["project"] = "Legacy.Tests", ["includeExisting"] = true });

        var text = await server.CallAsync("dotnet_rename", new() { ["symbol"] = "OrderCalculatorTests", ["newName"] = "CalculatorTests", ["apply"] = true });
        var check = await server.CallAsync("dotnet_check", new() { ["scope"] = "project", ["project"] = "Legacy.Tests" });

        // The copy's test project has errors at load (its MSTest package is not restored): after the move they are not new.
        Has(before, "OrderCalculatorTests.cs");
        Has(text, "renames Legacy.Tests/OrderCalculatorTests.cs → Legacy.Tests/CalculatorTests.cs");
        Has(text, "new since load: none");
        Has(check, "new since load: none");
    }

    [Fact]
    public async Task AC61_renaming_a_window_moves_its_xaml_and_the_names_of_it()
    {
        await using var server = await LegacyServer.StartAsync();
        Assert.SkipUnless(server.Available, "Needs Windows with Visual Studio Build Tools to load the legacy fixture.");

        var text = await server.CallAsync("dotnet_rename", new() { ["symbol"] = "MainWindow", ["newName"] = "OrdersWindow" });

        Has(text, "renames Legacy.Wpf/MainWindow.xaml.cs → Legacy.Wpf/OrdersWindow.xaml.cs");
        Has(text, "renames Legacy.Wpf/MainWindow.xaml → Legacy.Wpf/OrdersWindow.xaml");
        Assert.Contains("+<Window x:Class=\"Legacy.Wpf.OrdersWindow\"", text, StringComparison.Ordinal);
        Assert.Contains("+             StartupUri=\"OrdersWindow.xaml\">", text, StringComparison.Ordinal);
        Has(text, "+      <DependentUpon>OrdersWindow.xaml</DependentUpon>");
    }

    [Fact]
    public async Task AC61_rename_file_false_keeps_the_file_name()
    {
        await using var server = await LegacyServer.StartAsync();
        Assert.SkipUnless(server.Available, "Needs Windows with Visual Studio Build Tools to load the legacy fixture.");

        var text = await server.CallAsync("dotnet_rename", new() { ["symbol"] = "OrderCalculator", ["newName"] = "TotalCalculator", ["renameFile"] = false });

        Assert.DoesNotContain("renames ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Legacy.Core.csproj", text, StringComparison.Ordinal);
        Has(text, "+    public class TotalCalculator");
    }

    [Fact]
    public async Task AC65_a_name_that_already_exists_is_a_conflict_and_nothing_is_written()
    {
        await using var server = await LegacyServer.StartAsync();
        Assert.SkipUnless(server.Available, "Needs Windows with Visual Studio Build Tools to load the legacy fixture.");
        var before = await File.ReadAllTextAsync(server.Copy.PathOf("Legacy.Core", "Orders", "OrderCalculator.cs"), Ct);

        var (isError, text) = await server.Harness!.CallAsync("dotnet_rename", new() { ["symbol"] = "OrderCalculator.GetTotal", ["newName"] = "TaxRate", ["apply"] = true }, Ct);

        Assert.True(isError, text);
        Has(text, "'TaxRate' already exists");
        Has(text, $"{Calculator}:27");
        Assert.Equal(before, await File.ReadAllTextAsync(server.Copy.PathOf("Legacy.Core", "Orders", "OrderCalculator.cs"), Ct));
    }

    [Fact]
    public async Task AC65_apply_refuses_when_a_file_changed_since_the_diff()
    {
        await using var server = await LegacyServer.StartAsync();
        Assert.SkipUnless(server.Available, "Needs Windows with Visual Studio Build Tools to load the legacy fixture.");
        var arguments = new Dictionary<string, object?> { ["symbol"] = "OrderCalculator.GetTotal", ["newName"] = "CalculateTotal" };
        await server.CallAsync("dotnet_rename", arguments);

        var form = server.Copy.PathOf("Legacy.Desktop", "MainForm.cs");
        await File.WriteAllTextAsync(form, (await File.ReadAllTextAsync(form, Ct)) + "// edited after the diff\r\n", Ct);
        await server.CallUntilAsync("dotnet_check", [], t => t.Contains("MainForm.cs", StringComparison.Ordinal) || t.Contains("changed: 1 file", StringComparison.Ordinal));
        var (isError, text) = await server.Harness!.CallAsync("dotnet_rename", new(arguments) { ["apply"] = true }, Ct);

        Assert.True(isError, text);
        Has(text, "Legacy.Desktop/MainForm.cs changed since the diff was shown");
        Assert.DoesNotContain("CalculateTotal", await File.ReadAllTextAsync(server.Copy.PathOf("Legacy.Core", "Orders", "OrderCalculator.cs"), Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AC65_read_only_refuses_apply_but_still_shows_the_diff()
    {
        await using var server = await LegacyServer.StartAsync(o => o.ReadOnly = true);
        Assert.SkipUnless(server.Available, "Needs Windows with Visual Studio Build Tools to load the legacy fixture.");
        var arguments = new Dictionary<string, object?> { ["symbol"] = "OrderCalculator.GetTotal", ["newName"] = "CalculateTotal" };

        var preview = await server.CallAsync("dotnet_rename", arguments);
        var (isError, text) = await server.Harness!.CallAsync("dotnet_rename", new(arguments) { ["apply"] = true }, Ct);

        Has(preview, "preview of rename");
        Assert.True(isError, text);
        Has(text, "Refused to write files");
    }

    [Fact]
    public async Task AC65_an_invalid_name_or_a_keyword_is_refused()
    {
        await using var server = await LegacyServer.StartAsync();
        Assert.SkipUnless(server.Available, "Needs Windows with Visual Studio Build Tools to load the legacy fixture.");

        var (badError, bad) = await server.Harness!.CallAsync("dotnet_rename", new() { ["symbol"] = "OrderCalculator.GetTotal", ["newName"] = "2total" }, Ct);
        var (keywordError, keyword) = await server.Harness!.CallAsync("dotnet_rename", new() { ["symbol"] = "OrderCalculator.GetTotal", ["newName"] = "class" }, Ct);

        Assert.True(badError, bad);
        Has(bad, "is not a valid identifier");
        Assert.True(keywordError, keyword);
        Has(keyword, "is a C# keyword");
    }
}
