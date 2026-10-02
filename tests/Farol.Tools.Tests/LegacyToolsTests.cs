using Farol.Testing;
using Xunit;

namespace Farol.Tools.Tests;

/// <summary>The legacy toolset through the protocol, on one loaded copy of the legacy fixture.</summary>
public sealed class LegacyToolsTests(LegacyMcpFixture fixture) : IClassFixture<LegacyMcpFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AC25_inventory_reports_technologies_with_counts_and_locations()
    {
        Assert.SkipUnless(fixture.Available, "Needs Windows with Visual Studio or Build Tools.");

        var text = await Call("dotnet_legacy_inventory", []);

        Assert.StartsWith("legacy inventory: Legacy.sln · 6 project(s): 6 classic (non-SDK), 0 SDK-style · targets: net48 (6) · packages.config: Legacy.Tests (3 packages) · VB.NET: Legacy.VbLib", text, StringComparison.Ordinal);
        Assert.Contains("- WebForms · Legacy.Web: 1 page, 4 code uses (Legacy.Web/Default.aspx, Legacy.Web/Default.aspx.cs:7, :11, :13, :21)", text, StringComparison.Ordinal);
        Assert.Contains("- ASMX web services · Legacy.Web: 1 service, 4 code uses (Legacy.Web/LegacyService.asmx,", text, StringComparison.Ordinal);
        Assert.Contains("- WCF · Legacy.Web: 1 service, 1 configured service, 2 code uses (Legacy.Web/OrderService.svc, Legacy.Web/Web.config,", text, StringComparison.Ordinal);
        Assert.Contains("- WinForms · Legacy.Desktop: 1 form (Legacy.Desktop/MainForm.cs:7)", text, StringComparison.Ordinal);
        Assert.Contains("- WPF · Legacy.Wpf: 2 XAML files (Legacy.Wpf/App.xaml, Legacy.Wpf/MainWindow.xaml)", text, StringComparison.Ordinal);
        Assert.Contains("- BinaryFormatter · Legacy.Core: 2 code uses (Legacy.Core/Serialization/LegacySerializer.cs:14, :26)", text, StringComparison.Ordinal);
        Assert.Contains(
            "- System.Configuration · Legacy.Core: 1 code use (Legacy.Core/Orders/OrderCalculator.cs:29) · Legacy.Web: 3 app settings, 2 connection strings (Legacy.Web/Web.config)",
            text,
            StringComparison.Ordinal);
        Assert.Contains("- binding redirects · Legacy.Web: 1 binding redirect (Legacy.Web/Web.config)", text, StringComparison.Ordinal);
        Assert.Contains("- Legacy.Core/Serialization/LegacySerializer.cs: 2 line(s) · BinaryFormatter", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AC26_config_inspect_masks_secrets_and_maps_to_appsettings_json()
    {
        Assert.SkipUnless(fixture.Available, "Needs Windows with Visual Studio or Build Tools.");

        var text = await Call("dotnet_config_inspect", new() { ["path"] = "Legacy.Web/Web.config" });
        var list = await Call("dotnet_config_inspect", []);

        Assert.StartsWith("config: Legacy.Web/Web.config", text, StringComparison.Ordinal);
        Assert.Contains("- TaxRate = 0.12", text, StringComparison.Ordinal);
        Assert.Contains("- PaymentApiKey = *** (masked)", text, StringComparison.Ordinal);
        Assert.Contains("- ReportingDb (System.Data.SqlClient): Server=reports.example.test;Database=Reports;User ID=reporter;Password=***; (secrets masked)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-password", text, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-not-a-real-key", text, StringComparison.Ordinal);
        Assert.Contains("- service Legacy.Web.OrderService: basicHttpBinding (Legacy.Web.IOrderService), mexHttpBinding (IMetadataExchange) at 'mex'", text, StringComparison.Ordinal);
        Assert.Contains("- Newtonsoft.Json 0.0.0.0-13.0.0.0 → 13.0.0.0", text, StringComparison.Ordinal);
        Assert.Contains("```json", text, StringComparison.Ordinal);
        Assert.Contains("  \"Reports\": {\n    \"PageSize\": \"50\"\n  },", text, StringComparison.Ordinal);
        Assert.Contains("- connectionStrings → ConnectionStrings:", text, StringComparison.Ordinal);
        Assert.Contains("- Legacy.Web/Web.config: 3 app setting(s), 2 connection string(s), 2 secret(s), WCF: 1 service(s), 0 client endpoint(s), 1 binding redirect(s)", list, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Config_inspect_reads_only_inside_the_trusted_directories()
    {
        Assert.SkipUnless(fixture.Available, "Needs Windows with Visual Studio or Build Tools.");

        var (isError, text) = await fixture.Harness!.CallAsync("dotnet_config_inspect", new() { ["path"] = "../outside/web.config" }, Ct);

        Assert.True(isError);
        Assert.Contains("resolves outside the directories Farol trusts", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AC27_portability_groups_missing_and_obsolete_apis_with_locations_and_replacements()
    {
        Assert.SkipUnless(fixture.Available, "Needs Windows with Visual Studio or Build Tools.");

        var text = await Call("dotnet_portability", []);

        Assert.StartsWith("portability: Legacy.sln → net10.0 · 6 .NET Framework project(s) checked · ", text, StringComparison.Ordinal);
        Assert.Contains("Legacy.Desktop (net48 → net10.0-windows, ", text, StringComparison.Ordinal);
        Assert.Contains("BinaryFormatter (Legacy.Core) → System.Text.Json, XmlSerializer or DataContractSerializer", text, StringComparison.Ordinal);
        Assert.Contains("- BinaryFormatter · obsolete (SYSLIB0011): BinaryFormatter serialization is obsolete", text, StringComparison.Ordinal);
        Assert.Contains("· Legacy.Core/Serialization/LegacySerializer.cs:14, :26", text, StringComparison.Ordinal);
        Assert.Contains("System.Configuration (Legacy.Core) → Microsoft.Extensions.Configuration", text, StringComparison.Ordinal);
        Assert.Contains("- ConfigurationManager.AppSettings · missing · Legacy.Core/Orders/OrderCalculator.cs:29", text, StringComparison.Ordinal);
        Assert.Contains("WebForms (Legacy.Web) → Razor Pages or Blazor", text, StringComparison.Ordinal);
        Assert.Contains("- Page · missing · Legacy.Web/Default.aspx.cs:7", text, StringComparison.Ordinal);
        Assert.Contains("ASP.NET (System.Web) (Legacy.Web) → ", text, StringComparison.Ordinal);
        Assert.Contains("- HttpApplication · missing · Legacy.Web/Global.asax.cs:6", text, StringComparison.Ordinal);
        Assert.Contains("WCF (Legacy.Web) → CoreWCF", text, StringComparison.Ordinal);
        Assert.DoesNotContain("WinForms (", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AC28_migration_plan_orders_steps_bottom_up_with_tasks()
    {
        Assert.SkipUnless(fixture.Available, "Needs Windows with Visual Studio or Build Tools.");

        var text = await Call("dotnet_migration_plan", new() { ["maxTokens"] = 6000 });

        Assert.StartsWith("migration plan: Legacy.sln → net10.0 · 6 step(s) · sizes: 1 L, 2 M, 3 S", text, StringComparison.Ordinal);
        Assert.Contains("\n1. Legacy.Core — library, net48 → net48;net10.0 · size M · after: nothing\n", text, StringComparison.Ordinal);
        Assert.Contains("\n6. Legacy.Web — web application, net48 → a new ASP.NET Core project (net10.0) · size L · after: Legacy.Core\n", text, StringComparison.Ordinal);
        Assert.Contains("   - BinaryFormatter: 3 API(s) missing or obsolete on net10.0", text, StringComparison.Ordinal);
        Assert.Contains("   - Move packages.config (3 package(s)) to PackageReference", text, StringComparison.Ordinal);
        Assert.Contains("   - Set `<TargetFramework>net10.0-windows</TargetFramework>` with `<UseWPF>true</UseWPF>`.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("wrote:", text, StringComparison.Ordinal);
    }

    private async Task<string> Call(string tool, Dictionary<string, object?> arguments)
    {
        var (isError, text) = await fixture.Harness!.CallAsync(tool, arguments, Ct);
        Assert.False(isError, text);
        return text;
    }
}

/// <summary>Writing the migration documents, on fresh copies.</summary>
public sealed class MigrationDocumentsToolTests
{
    [Fact]
    public async Task AC28_write_creates_the_documents_and_keeps_ticked_tasks()
    {
        await using var server = await LegacyServer.StartAsync();
        Assert.SkipUnless(server.Available, "Needs Windows with Visual Studio or Build Tools.");
        var folder = server.Copy.PathOf("docs", "modernization");

        var first = await server.CallAsync("dotnet_migration_plan", new() { ["write"] = true, ["maxTokens"] = 500 });
        var tasks = await File.ReadAllTextAsync(Path.Combine(folder, "tasks.md"), TestContext.Current.CancellationToken);
        const string Ticked = "- [ ] Set `<TargetFrameworks>net48;net10.0</TargetFrameworks>`.";
        await File.WriteAllTextAsync(Path.Combine(folder, "tasks.md"), tasks.Replace(Ticked, Ticked.Replace("[ ]", "[x]", StringComparison.Ordinal), StringComparison.Ordinal), TestContext.Current.CancellationToken);
        var second = await server.CallAsync("dotnet_migration_plan", new() { ["write"] = true, ["maxTokens"] = 500 });
        var rewritten = await File.ReadAllTextAsync(Path.Combine(folder, "tasks.md"), TestContext.Current.CancellationToken);

        Assert.Contains("wrote: docs/modernization/assessment.md (created), docs/modernization/plan.md (created), docs/modernization/tasks.md (created)", first, StringComparison.Ordinal);
        Assert.Contains("the full plan is in docs/modernization/plan.md", first, StringComparison.Ordinal);
        Assert.Contains("docs/modernization/tasks.md (updated)", second, StringComparison.Ordinal);
        Assert.Contains("# Modernization assessment — Legacy.sln → net10.0", await File.ReadAllTextAsync(Path.Combine(folder, "assessment.md"), TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Contains("### 1. Legacy.Core — library, net48 → net48;net10.0 (size M)", await File.ReadAllTextAsync(Path.Combine(folder, "plan.md"), TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Contains(Ticked.Replace("[ ]", "[x]", StringComparison.Ordinal), rewritten, StringComparison.Ordinal);
        Assert.Contains("- [ ] Convert Legacy.Core.csproj to an SDK-style project", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AC28_read_only_refuses_to_write_the_documents()
    {
        await using var server = await LegacyServer.StartAsync(o => o.ReadOnly = true);
        Assert.SkipUnless(server.Available, "Needs Windows with Visual Studio or Build Tools.");

        var (isError, text) = await server.Harness!.CallAsync("dotnet_migration_plan", new() { ["write"] = true }, TestContext.Current.CancellationToken);
        var plan = await server.CallAsync("dotnet_migration_plan", []);

        Assert.True(isError);
        Assert.Contains("Refused to write the migration plan: Farol was started with --read-only", text, StringComparison.Ordinal);
        Assert.Contains("Call without write=true to get the plan in the response.", text, StringComparison.Ordinal);
        Assert.False(Directory.Exists(server.Copy.PathOf("docs", "modernization")));
        Assert.StartsWith("migration plan: Legacy.sln → net10.0", plan, StringComparison.Ordinal);
    }
}
