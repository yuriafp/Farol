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

        // The files that call GetTotal are checked, in every project and both languages; no project is compiled whole.
        Assert.Contains("checked files (edited, and those using changed declarations): ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("checked projects:", text, StringComparison.Ordinal);
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

    [Fact]
    public async Task A_file_the_workspace_does_not_compile_is_named_as_given_with_the_reason()
    {
        await using var server = await LegacyServer.StartAsync();
        Assert.SkipUnless(server.Available, "Needs Windows with Visual Studio or Build Tools.");
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(server.Copy.PathOf("Legacy.Core", "Orders", "Unlisted.cs"), "namespace Legacy.Core.Orders { class Unlisted { } }", ct);

        var unlisted = await server.Harness!.CallAsync("dotnet_check", new() { ["path"] = "Legacy.Core/Orders/Unlisted.cs" }, ct);
        var missing = await server.Harness!.CallAsync("dotnet_check", new() { ["path"] = "Legacy.Core/Orders/Missing.cs" }, ct);

        Assert.True(unlisted.IsError);
        Assert.Contains("'Legacy.Core/Orders/Unlisted.cs' is not a source file of the workspace.", unlisted.Text, StringComparison.Ordinal);
        Assert.Contains("Legacy.Core.csproj is a classic project, which compiles only the files it lists", unlisted.Text, StringComparison.Ordinal);
        Assert.True(missing.IsError);
        Assert.Contains("'Legacy.Core/Orders/Missing.cs' is not a source file of the workspace.", missing.Text, StringComparison.Ordinal);
        Assert.Contains("No file has that path", missing.Text, StringComparison.Ordinal);
    }
}

/// <summary>
/// dotnet_check reads the files a call names before checking them, so one created a moment ago is checked; a file the
/// project leaves out stays out all the same, whichever call names it.
/// </summary>
public sealed class ExcludedFileTests
{
    [Fact]
    public async Task A_file_the_project_leaves_out_stays_out_when_a_check_names_it()
    {
        var ct = TestContext.Current.CancellationToken;
        using var copy = FixtureCopy.Create(TestPaths.ModernDirectory);
        var project = copy.PathOf("src", "Modern.Core", "Modern.Core.csproj");
        var excluding = (await File.ReadAllTextAsync(project, ct)).Replace("</Project>", "  <ItemGroup>\n    <Compile Remove=\"Drafts/**\" />\n  </ItemGroup>\n</Project>", StringComparison.Ordinal);
        await File.WriteAllTextAsync(project, excluding, ct);
        const string broken = "namespace Modern.Core;\n\npublic static class Broken\n{\n    public static int One() => \"one\";\n}\n";
        Directory.CreateDirectory(copy.PathOf("src", "Modern.Core", "Drafts"));
        Directory.CreateDirectory(copy.PathOf("src", "Modern.Core", "obj"));
        await File.WriteAllTextAsync(copy.PathOf("src", "Modern.Core", "Drafts", "Draft.cs"), broken, ct);
        await File.WriteAllTextAsync(copy.PathOf("src", "Modern.Core", "obj", "Stale.cs"), broken, ct);
        await FixtureRestore.EnsureRestoredAsync(copy.PathOf("Modern.slnx"), ct);
        await using var harness = await McpHarness.StartAsync(copy.Root, ct);

        var draft = await harness.CallAsync("dotnet_check", new() { ["path"] = "src/Modern.Core/Drafts/Draft.cs" }, ct);
        var stale = await harness.CallAsync("dotnet_check", new() { ["path"] = "src/Modern.Core/obj/Stale.cs" }, ct);
        var edited = await harness.CallAsync("dotnet_check", new() { ["edited"] = "src/Modern.Core/Drafts/Draft.cs" }, ct);
        await File.WriteAllTextAsync(copy.PathOf("src", "Modern.Core", "obj", "Late.cs"), broken, ct);
        var late = await harness.CallAsync("dotnet_check", new() { ["edited"] = "src/Modern.Core/obj/Late.cs" }, ct);

        Assert.True(draft.IsError);
        Assert.Contains("'src/Modern.Core/Drafts/Draft.cs' is not a source file of the workspace. Modern.Core.csproj leaves it out", draft.Text, StringComparison.Ordinal);
        Assert.True(stale.IsError);
        Assert.Contains("'src/Modern.Core/obj/Stale.cs' is not a source file of the workspace. Modern.Core.csproj leaves it out", stale.Text, StringComparison.Ordinal);
        Assert.False(edited.IsError, edited.Text);
        Assert.Contains("no source files changed since the workspace loaded", edited.Text, StringComparison.Ordinal);
        Assert.False(late.IsError, late.Text);
        Assert.Contains("no source files changed since the workspace loaded", late.Text, StringComparison.Ordinal); // new, but build output
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
