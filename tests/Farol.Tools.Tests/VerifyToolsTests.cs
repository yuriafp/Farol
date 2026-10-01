using Farol.Testing;
using Xunit;

namespace Farol.Tools.Tests;

/// <summary>dotnet_check through the protocol: only what the edits introduced, wherever it surfaces.</summary>
public sealed class CheckToolTests
{
    private static readonly string[] Calculator = ["Legacy.Core", "Orders", "OrderCalculator.cs"];

    [Fact]
    public async Task AC14_check_reports_a_new_type_error_and_no_preexisting_diagnostic()
    {
        await using var server = await LegacyServer.StartAsync();
        Assert.SkipUnless(server.Available, "Needs Windows with Visual Studio or Build Tools.");

        await server.EditAsync(Calculator, ("return order.Subtotal() * (1 + TaxRate());", "return \"total\";"));
        var text = await server.CallUntilAsync("dotnet_check", [], t => t.Contains("CS0029", StringComparison.Ordinal));

        Assert.Contains("new since load: 1 error(s)", text, StringComparison.Ordinal);
        Assert.Contains("- Legacy.Core/Orders/OrderCalculator.cs:23 · error CS0029 · Cannot implicitly convert type 'string' to 'decimal'", text, StringComparison.Ordinal);
        Assert.DoesNotContain("CS0168", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AC15_check_reports_the_vb_caller_a_csharp_signature_change_breaks()
    {
        await using var server = await LegacyServer.StartAsync();
        Assert.SkipUnless(server.Available, "Needs Windows with Visual Studio or Build Tools.");

        await server.EditAsync(Calculator, ("public decimal GetTotal(int orderId)", "public decimal GetTotal(int orderId, bool includeTax)"));
        var text = await server.CallUntilAsync("dotnet_check", [], t => t.Contains("ShippingCalculator.vb", StringComparison.Ordinal));

        Assert.Contains("Legacy.VbLib:", text, StringComparison.Ordinal);
        Assert.Matches(@"- Legacy\.VbLib/ShippingCalculator\.vb:13 · error BC\d+ · .*includeTax", text);
        Assert.Contains("checked projects: Legacy.Core, Legacy.Desktop, Legacy.VbLib, Legacy.Web, Legacy.Wpf", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AC16_existing_diagnostics_are_not_new_and_are_listed_separately_on_request()
    {
        await using var server = await LegacyServer.StartAsync();
        Assert.SkipUnless(server.Available, "Needs Windows with Visual Studio or Build Tools.");

        // Unrelated edits: a body change elsewhere, and a line inserted above the warning that existed at load.
        await server.EditAsync(Calculator, ("0.1m", "0.2m"));
        await server.EditAsync(["Legacy.Core", "Serialization", "LegacySerializer.cs"], ("        // The swallowed", "        // An unrelated comment.\n        // The swallowed"));
        var text = await server.CallUntilAsync("dotnet_check", [], t => t.Contains("changed: 2 file(s)", StringComparison.Ordinal));
        var withExisting = await server.CallAsync("dotnet_check", new() { ["includeExisting"] = true });

        Assert.Contains("new since load: none", text, StringComparison.Ordinal);
        Assert.DoesNotContain("CS0168", text, StringComparison.Ordinal);
        Assert.Contains("new since load: none", withExisting, StringComparison.Ordinal);
        Assert.Contains("existing at load, still present: 1 warning(s)", withExisting, StringComparison.Ordinal);
        Assert.Contains("- Legacy.Core/Serialization/LegacySerializer.cs:30 · warning CS0168 · The variable 'ex' is declared but never used", withExisting, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_changed_since_load_says_so()
    {
        await using var server = await LegacyServer.StartAsync();
        Assert.SkipUnless(server.Available, "Needs Windows with Visual Studio or Build Tools.");

        var text = await server.CallAsync("dotnet_check", []);
        var project = await server.CallAsync("dotnet_check", new() { ["project"] = "Legacy.Core", ["includeExisting"] = true });

        Assert.Contains("no source files changed since the workspace loaded", text, StringComparison.Ordinal);
        Assert.Contains("checked projects: Legacy.Core", project, StringComparison.Ordinal);
        Assert.Contains("warning CS0168", project, StringComparison.Ordinal);
    }
}

/// <summary>dotnet_code_actions through the protocol: list, preview as a diff, apply with a fresh check, and read-only.</summary>
public sealed class CodeActionsToolTests
{
    private const string CalculatorPath = "Legacy.Core/Orders/OrderCalculator.cs";

    [Fact]
    public async Task AC17_AC18_the_add_using_fix_previews_as_a_diff_then_applies_with_a_fresh_check()
    {
        await using var server = await LegacyServer.StartAsync();
        Assert.SkipUnless(server.Available, "Needs Windows with Visual Studio or Build Tools.");
        var file = server.Copy.PathOf("Legacy.Core", "Orders", "OrderCalculator.cs");
        await server.EditAsync(
            ["Legacy.Core", "Orders", "OrderCalculator.cs"],
            ("            return order.Subtotal() * (1 + TaxRate());", "            var log = new StringBuilder();\n            return order.Subtotal() * (1 + TaxRate());"));
        await server.CallUntilAsync("dotnet_check", [], t => t.Contains("CS0246", StringComparison.Ordinal));

        var list = await server.CallAsync("dotnet_code_actions", new() { ["path"] = CalculatorPath, ["line"] = 23 });
        var preview = await server.CallAsync("dotnet_code_actions", new() { ["path"] = CalculatorPath, ["line"] = 23, ["action"] = "using System.Text;" });
        var untouched = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        var applied = await server.CallAsync("dotnet_code_actions", new() { ["path"] = CalculatorPath, ["line"] = 23, ["action"] = "using System.Text;", ["apply"] = true });
        var written = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);

        Assert.Contains($"error CS0246 at {CalculatorPath}:23: The type or namespace name 'StringBuilder' could not be found", list, StringComparison.Ordinal);
        Assert.Contains("using System.Text; · fix CS0246 · add import", list, StringComparison.Ordinal);

        Assert.Contains("diff only — no file was changed", preview, StringComparison.Ordinal);
        Assert.Contains($"--- a/{CalculatorPath}\n+++ b/{CalculatorPath}", preview, StringComparison.Ordinal);
        Assert.Contains("\n+using System.Text;\n", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("using System.Text;", untouched, StringComparison.Ordinal);

        Assert.Contains($"applied 'using System.Text;' (fix CS0246 · add import) at {CalculatorPath}:23: wrote {CalculatorPath}", applied, StringComparison.Ordinal);
        Assert.Contains("check after applying:\nnew since load: none", applied, StringComparison.Ordinal);
        Assert.Contains("using System.Text;", written, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(file)!, "*.tmp"));
    }

    [Fact]
    public async Task AC18_apply_is_refused_in_read_only_mode_with_the_reason()
    {
        var ct = TestContext.Current.CancellationToken;
        using var copy = FixtureCopy.Create(TestPaths.LegacyDirectory);
        await using var harness = await McpHarness.StartAsync(copy.Root, ct, o => o.ReadOnly = true);

        var (isError, text) = await harness.CallAsync(
            "dotnet_code_actions", new() { ["path"] = CalculatorPath, ["line"] = 23, ["action"] = "1", ["apply"] = true }, ct);

        Assert.True(isError);
        Assert.Contains("Refused to write files: Farol was started with --read-only", text, StringComparison.Ordinal);
        Assert.Contains("without apply=true", text, StringComparison.Ordinal);
    }
}

/// <summary>AC-32 and the path sandbox: nothing outside the root or Farol:TrustedPaths is loaded, read or written.</summary>
public sealed class TrustTests
{
    [Fact]
    public async Task AC32_a_solution_outside_the_root_is_refused_with_an_actionable_error()
    {
        var ct = TestContext.Current.CancellationToken;
        using var root = FixtureCopy.Create(TestPaths.ModernDirectory);
        using var outside = FixtureCopy.Create(TestPaths.ModernDirectory);
        await using var harness = await McpHarness.StartAsync(root.Root, ct);

        var (isError, text) = await harness.CallAsync("dotnet_overview", new() { ["workspace"] = outside.PathOf("Modern.slnx") }, ct);

        Assert.True(isError);
        Assert.Contains("Workspace", text, StringComparison.Ordinal);
        Assert.Contains("resolves outside the directories Farol trusts", text, StringComparison.Ordinal);
        Assert.Contains("Farol:TrustedPaths", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Trusted_paths_admit_a_solution_outside_the_root()
    {
        var ct = TestContext.Current.CancellationToken;
        using var root = FixtureCopy.Create(TestPaths.ModernDirectory);
        using var outside = FixtureCopy.Create(TestPaths.ModernDirectory);
        await using var harness = await McpHarness.StartAsync(root.Root, ct, o => o.TrustedPaths.Add(outside.Root));

        var (isError, text) = await harness.CallAsync("dotnet_workspace", new() { ["workspace"] = outside.PathOf("Modern.slnx") }, ct);

        Assert.False(isError, text);
        Assert.Contains("workspace: ", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dotnet_outline")]
    [InlineData("dotnet_code_actions")]
    [InlineData("dotnet_check")]
    public async Task Path_arguments_cannot_leave_the_root(string tool)
    {
        var ct = TestContext.Current.CancellationToken;
        using var root = FixtureCopy.Create(TestPaths.ModernDirectory);
        await using var harness = await McpHarness.StartAsync(root.Root, ct);

        var arguments = new Dictionary<string, object?> { ["path"] = "../elsewhere/Program.cs" };
        if (tool == "dotnet_code_actions")
        {
            arguments["line"] = 1;
        }

        var (isError, text) = await harness.CallAsync(tool, arguments, ct);

        Assert.True(isError);
        Assert.Contains("Path '../elsewhere/Program.cs' resolves outside the directories Farol trusts", text, StringComparison.Ordinal);
    }
}
