using Farol.Core;
using Farol.Engine.Legacy;
using Farol.Testing;
using NuGet.Frameworks;
using Xunit;

namespace Farol.Engine.Tests;

/// <summary>Inventory, portability and the migration plan on the legacy fixture (Windows with Visual Studio).</summary>
[Collection(LegacyFixtureDefinition.Name)]
public sealed class LegacyAnalysisTests(LegacyWorkspaceFixture fixture)
{
    private static string Packages => NuGet.Configuration.SettingsUtility.GetGlobalPackagesFolder(NuGet.Configuration.Settings.LoadDefaultSettings(TestPaths.LegacyDirectory));

    [Fact]
    public async Task AC25_inventory_counts_the_legacy_technologies_with_their_locations()
    {
        Assert.SkipUnless(fixture.Available, "Needs Windows with Visual Studio or Build Tools.");
        var ct = TestContext.Current.CancellationToken;
        var target = new Workspaces.WorkspaceTarget(TestPaths.LegacySolution, Workspaces.WorkspaceTargetKind.Solution);

        var inventory = await LegacyInventory.BuildAsync(fixture.Snapshot!, target, report: null, projectName: null, ct);

        Assert.Equal(6, inventory.Projects.Count(p => !p.Overview.IsSdkStyle));
        Assert.Equal(["1 page"], Item(inventory, "WebForms", "Legacy.Web").Counts.Take(1).Select(c => c.ToString()));
        Assert.Contains(Item(inventory, "ASMX web services", "Legacy.Web").Locations, l => l.EndsWith("LegacyService.asmx", StringComparison.Ordinal));
        Assert.Contains("1 service", Item(inventory, "WCF", "Legacy.Web").Counts.Select(c => c.ToString()));
        Assert.Contains("1 configured service", Item(inventory, "WCF", "Legacy.Web").Counts.Select(c => c.ToString()));
        Assert.Equal(["1 form"], Item(inventory, "WinForms", "Legacy.Desktop").Counts.Select(c => c.ToString()));
        Assert.Equal(["2 XAML files"], Item(inventory, "WPF", "Legacy.Wpf").Counts.Select(c => c.ToString()));
        var binaryFormatter = Item(inventory, "BinaryFormatter", "Legacy.Core");
        Assert.Equal(["2 code uses"], binaryFormatter.Counts.Select(c => c.ToString()));
        Assert.Equal([":14", ":26"], binaryFormatter.Locations.Select(l => l[l.LastIndexOf(':')..]));
        Assert.Equal(["1 code use"], Item(inventory, "System.Configuration", "Legacy.Core").Counts.Select(c => c.ToString()));
        Assert.Equal(["1 binding redirect"], Item(inventory, "binding redirects", "Legacy.Web").Counts.Select(c => c.ToString()));
        Assert.Equal(3, inventory.Projects.Single(p => p.Overview.Name == "Legacy.Tests").PackagesConfigPackages);
        Assert.DoesNotContain(inventory.Items, i => i.Locations.Any(l => l.Contains(".designer.", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task AC27_portability_reports_missing_and_obsolete_apis_grouped_by_technology()
    {
        Assert.SkipUnless(fixture.Available, "Needs Windows with Visual Studio or Build Tools.");
        var ct = TestContext.Current.CancellationToken;

        var report = await PortabilityAnalyzer.AnalyzeAsync(fixture.Snapshot!, NuGetFramework.Parse("net10.0"), Packages, projectName: null, progress: null, ct);

        var binary = Assert.Single(report.Issues, i => i.Api.Id == "T:System.Runtime.Serialization.Formatters.Binary.BinaryFormatter");
        Assert.Equal((ApiStatus.Obsolete, "SYSLIB0011"), (binary.Availability.Status, binary.Availability.DiagnosticId));
        Assert.Equal("BinaryFormatter", binary.Technology?.Name);
        Assert.Equal([14, 26], binary.Api.Locations.Select(l => l.Line));
        var appSettings = Assert.Single(report.Issues, i => i.Api.Id == "P:System.Configuration.ConfigurationManager.AppSettings");
        Assert.Equal(ApiStatus.Missing, appSettings.Availability.Status);
        Assert.Equal("System.Configuration", appSettings.Technology?.Name);
        Assert.Equal("WebForms", Assert.Single(report.Issues, i => i.Api.Id == "T:System.Web.UI.Page").Technology?.Name);
        Assert.Equal("ASP.NET (System.Web)", Assert.Single(report.Issues, i => i.Api.Id == "T:System.Web.HttpApplication").Technology?.Name);
        Assert.Equal("WCF", Assert.Single(report.Issues, i => i.Api.Id == "T:System.ServiceModel.ServiceContractAttribute").Technology?.Name);

        // WinForms and WPF run on net10.0-windows: their projects are checked there, and pass.
        Assert.Equal("net10.0-windows", report.Projects.Single(p => p.Name == "Legacy.Desktop").To);
        Assert.Empty(report.IssuesOf("Legacy.Desktop"));
        Assert.Empty(report.IssuesOf("Legacy.Wpf"));
        Assert.Empty(report.IssuesOf("Legacy.VbLib"));
    }

    [Fact]
    public async Task AC28_the_plan_goes_bottom_up_with_tasks_from_inventory_and_portability()
    {
        Assert.SkipUnless(fixture.Available, "Needs Windows with Visual Studio or Build Tools.");
        var ct = TestContext.Current.CancellationToken;
        var target = new Workspaces.WorkspaceTarget(TestPaths.LegacySolution, Workspaces.WorkspaceTargetKind.Solution);
        var inventory = await LegacyInventory.BuildAsync(fixture.Snapshot!, target, report: null, projectName: null, ct);
        var portability = await PortabilityAnalyzer.AnalyzeAsync(fixture.Snapshot!, NuGetFramework.Parse("net10.0"), Packages, projectName: null, progress: null, ct);

        var plan = MigrationPlanner.Build("Legacy.sln", inventory, portability, NuGetFramework.Parse("net10.0"), TestPaths.LegacyDirectory);

        Assert.Equal("Legacy.Core", plan.Steps[0].Project);
        Assert.Equal("Legacy.Web", plan.Steps[^1].Project);
        Assert.All(plan.Steps.Skip(1), s => Assert.Contains("Legacy.Core", s.DependsOn));
        var core = plan.Steps[0];
        Assert.Equal(("library", "net48;net10.0", "M"), (core.Kind, core.To, core.Size));
        Assert.Contains(core.Tasks, t => t.StartsWith("BinaryFormatter: 3 API(s) missing or obsolete on net10.0", StringComparison.Ordinal) && t.Contains("Legacy.Core/Serialization/LegacySerializer.cs:14, :26", StringComparison.Ordinal));
        Assert.Contains(core.Tasks, t => t.StartsWith("System.Configuration:", StringComparison.Ordinal));
        var web = plan.Steps[^1];
        Assert.Equal(("web application", "L"), (web.Kind, web.Size));
        Assert.Contains(web.Tasks, t => t.StartsWith("WebForms: 1 page (Legacy.Web/Default.aspx)", StringComparison.Ordinal));
        Assert.Contains(web.Tasks, t => t.StartsWith("Move 3 app setting(s) and 2 connection string(s) from Legacy.Web/Web.config", StringComparison.Ordinal));
        Assert.Equal("net10.0-windows", plan.Steps.Single(s => s.Project == "Legacy.Wpf").To);
        Assert.Equal("M", plan.Steps.Single(s => s.Project == "Legacy.Tests").Size); // packages.config

        var tasks = MigrationDocuments.Tasks(plan, DateTimeOffset.UnixEpoch, MigrationDocuments.Ticked($"- [x] {core.Tasks[1]}\n- [ ] {core.Tasks[0]}"));
        Assert.Contains($"- [x] {core.Tasks[1]}", tasks, StringComparison.Ordinal);
        Assert.Contains($"- [ ] {core.Tasks[0]}", tasks, StringComparison.Ordinal);
    }

    private static InventoryItem Item(LegacyInventoryReport inventory, string technology, string project) =>
        Assert.Single(inventory.Items, i => i.Technology == technology && i.Project == project);
}

public sealed class LegacyCatalogTests
{
    [Theory]
    [InlineData("System.Windows.Forms.Form", "WinForms")]
    [InlineData("System.Windows.Window", "WPF")]
    [InlineData("System.Web.UI.Page", "WebForms")]
    [InlineData("System.Web.UI.WebControls.Label.Text", "WebForms")]
    [InlineData("System.Web.HttpContext.Current", "ASP.NET (System.Web)")]
    [InlineData("System.Web.Services.WebMethodAttribute", "ASMX web services")]
    [InlineData("System.AppDomain.CreateDomain", "AppDomains")]
    [InlineData("System.Configuration.ConfigurationManager.AppSettings", "System.Configuration")]
    [InlineData("System.Runtime.Serialization.Formatters.Binary.BinaryFormatter.Serialize", "BinaryFormatter")]
    public void The_longest_matching_prefix_names_the_technology(string api, string technology) =>
        Assert.Equal(technology, LegacyCatalog.Match(api)?.Name);

    [Theory]
    [InlineData("System.AppDomain.CurrentDomain")]
    [InlineData("System.WebUtility")]
    [InlineData("System.String")]
    public void Apis_outside_the_catalog_have_no_technology(string api) => Assert.Null(LegacyCatalog.Match(api));
}

public sealed class TargetSurfaceTests
{
    private static string Packages => NuGet.Configuration.SettingsUtility.GetGlobalPackagesFolder(NuGet.Configuration.Settings.LoadDefaultSettings(TestPaths.RepoRoot));

    [Fact]
    public void The_target_surface_tells_missing_obsolete_and_available_apis_apart()
    {
        var net10 = TargetSurface.Load(TargetSurface.ParseTarget("net10.0"), windowsDesktop: false, Packages);

        Assert.Equal(ApiStatus.Available, net10.Check("T:System.IO.MemoryStream").Status);
        Assert.Equal(ApiStatus.Missing, net10.Check("T:System.Configuration.ConfigurationManager").Status);
        var binary = net10.Check("M:System.Runtime.Serialization.Formatters.Binary.BinaryFormatter.Serialize(System.IO.Stream,System.Object)");
        Assert.Equal((ApiStatus.Obsolete, "SYSLIB0011"), (binary.Status, binary.DiagnosticId));
        Assert.Equal(ApiStatus.Missing, net10.Check("T:System.Windows.Forms.Form").Status);
        Assert.StartsWith("Microsoft.NETCore.App.Ref 10.", net10.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_targets_include_winforms_and_wpf()
    {
        var windows = TargetSurface.Load(TargetSurface.ParseTarget("net10.0-windows"), windowsDesktop: false, Packages);

        Assert.Equal("net10.0-windows", windows.TargetFramework);
        Assert.Equal(ApiStatus.Available, windows.Check("T:System.Windows.Forms.Form").Status);
        Assert.Equal(ApiStatus.Available, windows.Check("T:System.Windows.Window").Status);
        var dataGrid = windows.Check("T:System.Windows.Forms.DataGrid");
        Assert.Equal((ApiStatus.Obsolete, "WFDEV006"), (dataGrid.Status, dataGrid.DiagnosticId));
    }

    [Theory]
    [InlineData("net48")]
    [InlineData("netcoreapp3.1")]
    [InlineData("not-a-framework")]
    public void Only_modern_dotnet_and_netstandard_are_migration_targets(string target)
    {
        var error = Assert.Throws<FarolException>(() => TargetSurface.ParseTarget(target));
        Assert.Equal(ErrorCodes.InvalidArgument, error.Code);
    }
}

/// <summary>Configuration files written to a temp folder: masking and the appsettings.json mapping.</summary>
public sealed class ConfigInspectorTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("farol-config-");

    [Fact]
    public void Secrets_are_masked_wherever_they_hide()
    {
        var path = Write("app.config", """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <appSettings>
                <add key="PageSize" value="20" />
                <add key="SmtpPassword" value="hunter2" />
                <add key="EncryptionKey" value="abc123" />
                <add key="Legacy" value="Server=x;Database=y;User Id=u;Pwd=hunter3" />
              </appSettings>
              <connectionStrings>
                <add name="Main" connectionString="Server=db;Database=App;User ID=app;Password=hunter4;" providerName="System.Data.SqlClient" />
                <add name="Ef" connectionString="metadata=res://*;provider connection string=&quot;data source=db;password=hunter5;MultipleActiveResultSets=True&quot;" providerName="System.Data.EntityClient" />
              </connectionStrings>
              <system.web>
                <machineKey validationKey="0123456789ABCDEF" decryptionKey="FEDCBA9876543210" />
              </system.web>
            </configuration>
            """);

        var config = ConfigInspector.Read(path);
        var everything = string.Join('\n', config.AppSettings.Select(s => s.Value).Concat(config.ConnectionStrings.Select(c => c.Value)).Concat(config.SystemWeb).Append(config.AppSettingsJson));

        Assert.DoesNotContain("hunter", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("0123456789ABCDEF", everything, StringComparison.Ordinal);
        Assert.Equal(["20", "***", "***", "Server=x;Database=y;User Id=u;Pwd=***"], config.AppSettings.Select(s => s.Value));
        Assert.Equal("Server=db;Database=App;User ID=app;Password=***;", config.ConnectionStrings[0].Value);
        Assert.Contains("password=***;MultipleActiveResultSets=True", config.ConnectionStrings[1].Value, StringComparison.Ordinal);
        Assert.Equal(5, config.MaskedCount);
        Assert.Contains("machineKey (keys masked)", config.SystemWeb);
    }

    [Fact]
    public void App_settings_map_to_json_sections_and_connection_strings_to_their_section()
    {
        var path = Write("web.config", """
            <configuration>
              <appSettings>
                <add key="Reports:PageSize" value="50" />
                <add key="Reports:Title" value="Orders" />
                <add key="Theme" value="dark" />
              </appSettings>
              <connectionStrings>
                <add name="Db" connectionString="Server=(localdb)\MSSQLLocalDB;Database=App" />
              </connectionStrings>
              <runtime>
                <assemblyBinding xmlns="urn:schemas-microsoft-com:asm.v1">
                  <dependentAssembly>
                    <assemblyIdentity name="Newtonsoft.Json" publicKeyToken="30ad4fe6b2a6aeed" />
                    <bindingRedirect oldVersion="0.0.0.0-13.0.0.0" newVersion="13.0.0.0" />
                  </dependentAssembly>
                </assemblyBinding>
              </runtime>
            </configuration>
            """);
        Write("web.Release.config", "<configuration />");

        var config = ConfigInspector.Read(path);
        var json = System.Text.Json.Nodes.JsonNode.Parse(config.AppSettingsJson)!;

        Assert.Equal("50", (string?)json["Reports"]!["PageSize"]);
        Assert.Equal("Orders", (string?)json["Reports"]!["Title"]);
        Assert.Equal("dark", (string?)json["Theme"]);
        Assert.Equal(@"Server=(localdb)\MSSQLLocalDB;Database=App", (string?)json["ConnectionStrings"]!["Db"]);
        Assert.Equal(new ConfigBindingRedirect("Newtonsoft.Json", "0.0.0.0-13.0.0.0", "13.0.0.0"), Assert.Single(config.BindingRedirects));
        Assert.Equal(["web.Release.config"], config.Transforms);
        Assert.Contains(config.Mapping, m => m.StartsWith("runtime/assemblyBinding (1 binding redirect(s)) → not needed", StringComparison.Ordinal));
    }

    [Fact]
    public void AC26_the_fixture_web_config_shows_settings_masked_wcf_and_redirect()
    {
        var config = ConfigInspector.Read(Path.Combine(TestPaths.LegacyDirectory, "Legacy.Web", "Web.config"));

        Assert.Equal(["TaxRate", "Reports:PageSize", "PaymentApiKey"], config.AppSettings.Select(s => s.Key));
        Assert.True(config.AppSettings.Single(s => s.Key == "PaymentApiKey").Masked);
        Assert.Contains("Password=***", config.ConnectionStrings.Single(c => c.Name == "ReportingDb").Value, StringComparison.Ordinal);
        Assert.Equal(["Legacy.Web.OrderService: basicHttpBinding (Legacy.Web.IOrderService), mexHttpBinding (IMetadataExchange) at 'mex'"], config.ServiceModel!.Services);
        Assert.Contains("aspNetCompatibilityEnabled=true", config.ServiceModel.Hosting);
        Assert.Equal("Newtonsoft.Json", Assert.Single(config.BindingRedirects).Assembly);
        Assert.DoesNotContain("fixture-password", config.AppSettingsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-not-a-real-key", config.AppSettingsJson, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_is_not_a_configuration_file_is_refused()
    {
        var path = Write("packages.config", "<packages />");

        var error = Assert.Throws<FarolException>(() => ConfigInspector.Read(path));

        Assert.Contains("is not a .NET configuration file", error.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            _root.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best effort.
        }
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_root.FullName, name);
        File.WriteAllText(path, content);
        return path;
    }
}
