using Farol.Engine.Navigation;
using Farol.Engine.Workspaces;
using Farol.Testing;
using Xunit;

namespace Farol.Engine.Tests;

/// <summary>References the compiler never sees, on the legacy fixture: WebForms, ASMX/WCF/Global.asax directives and XAML.</summary>
[Collection(LegacyFixtureDefinition.Name)]
public sealed class MarkupReferenceTests(LegacyWorkspaceFixture fixture)
{
    [Fact]
    public async Task AC7_webforms_event_handler_is_referenced_from_the_aspx()
    {
        Assert.SkipUnless(fixture.Available, "Needs Windows with Visual Studio or Build Tools.");

        var hit = Assert.Single(await MarkupHitsAsync(fixture.Snapshot!, fixture.Root, "_Default.btnCalculate_Click"));

        Assert.EndsWith(Path.Combine("Legacy.Web", "Default.aspx"), hit.FilePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(12, hit.Line);
        Assert.Contains("OnClick=\"btnCalculate_Click\"", hit.Snippet, StringComparison.Ordinal);
        Assert.Equal("Legacy.Web", hit.Project);
    }

    [Fact]
    public async Task AC8_wpf_event_handler_is_referenced_from_the_xaml()
    {
        Assert.SkipUnless(fixture.Available, "Needs Windows with Visual Studio or Build Tools.");

        var hit = Assert.Single(await MarkupHitsAsync(fixture.Snapshot!, fixture.Root, "MainWindow.OnCalculateClick"));

        Assert.EndsWith("MainWindow.xaml", hit.FilePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(10, hit.Line);
        Assert.Equal("Legacy.Wpf", hit.Project);
    }

    [Fact]
    public async Task Converter_declared_only_in_xaml_is_referenced()
    {
        Assert.SkipUnless(fixture.Available, "Needs Windows with Visual Studio or Build Tools.");
        var candidate = Assert.Single(await SymbolLocator.ResolveAsync(fixture.Snapshot!, fixture.Root, "PriceConverter", TestContext.Current.CancellationToken));

        var result = await ReferenceFinder.FindAsync(fixture.Snapshot!, candidate, TestContext.Current.CancellationToken);

        var hit = Assert.Single(result.References);
        Assert.Equal("markup", hit.Kind);
        Assert.Equal(7, hit.Line);
    }

    [Fact]
    public async Task Page_method_called_only_from_an_expression_is_referenced()
    {
        Assert.SkipUnless(fixture.Available, "Needs Windows with Visual Studio or Build Tools.");

        var hit = Assert.Single(await MarkupHitsAsync(fixture.Snapshot!, fixture.Root, "_Default.Greeting"));

        Assert.Equal(9, hit.Line);
    }

    [Theory]
    [InlineData("_Default", "Default.aspx")]
    [InlineData("LegacyService", "LegacyService.asmx")]
    [InlineData("Legacy.Web.OrderService", "OrderService.svc")]
    [InlineData("Global", "Global.asax")]
    [InlineData("MainWindow", "MainWindow.xaml")]
    public async Task Classes_named_by_directives_and_xclass_are_referenced(string symbol, string file)
    {
        Assert.SkipUnless(fixture.Available, "Needs Windows with Visual Studio or Build Tools.");

        var hit = Assert.Single(await MarkupHitsAsync(fixture.Snapshot!, fixture.Root, symbol));

        Assert.EndsWith(file, hit.FilePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, hit.Line);
    }

    [Theory]
    [InlineData("_Default.Page_Load", "Default.aspx")]
    [InlineData("Global.Application_Start", "Global.asax")]
    public async Task Convention_wired_events_are_referenced_from_their_directive(string symbol, string file)
    {
        Assert.SkipUnless(fixture.Available, "Needs Windows with Visual Studio or Build Tools.");

        var hit = Assert.Single(await MarkupHitsAsync(fixture.Snapshot!, fixture.Root, symbol));

        Assert.EndsWith(file, hit.FilePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, hit.Line);
    }

    [Theory]
    [InlineData("_Default.lblTotal", "Default.aspx", 11)]
    [InlineData("MainWindow.TotalText", "MainWindow.xaml", 11)]
    public async Task Controls_and_named_elements_are_referenced(string symbol, string file, int line)
    {
        Assert.SkipUnless(fixture.Available, "Needs Windows with Visual Studio or Build Tools.");

        var hit = Assert.Single(await MarkupHitsAsync(fixture.Snapshot!, fixture.Root, symbol));

        Assert.EndsWith(file, hit.FilePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(line, hit.Line);
    }

    internal static async Task<List<SourceHit>> MarkupHitsAsync(WorkspaceSnapshot snapshot, string root, string symbol)
    {
        var ct = TestContext.Current.CancellationToken;
        var candidate = Assert.Single(await SymbolLocator.ResolveAsync(snapshot, root, symbol, ct));
        var result = await ReferenceFinder.FindAsync(snapshot, candidate, ct);
        return [.. result.References.Where(r => r.Kind == "markup")];
    }
}

/// <summary>Markup edits reach the index through the file watcher, without reloading the solution.</summary>
public sealed class MarkupSyncTests
{
    [Fact]
    public async Task Editing_a_page_updates_markup_references_without_a_reload()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Classic .NET Framework projects need Windows.");
        var ct = TestContext.Current.CancellationToken;
        using var copy = FixtureCopy.Create(TestPaths.LegacyDirectory);
        await using var engine = EngineHarness.Create(copy.Root);
        var toolchain = await engine.Toolchain.ProbeAsync(copy.Root, ct);
        Assert.SkipWhen(toolchain.PreferredVisualStudio is null, "Visual Studio or Build Tools MSBuild is not installed.");

        var session = engine.Workspaces.GetSession(null);
        var loaded = await session.GetSnapshotAsync(wait: true, ct);
        var report = session.Report;
        var before = await MarkupReferenceTests.MarkupHitsAsync(loaded, copy.Root, "_Default.Greeting");

        var page = copy.PathOf("Legacy.Web", "Default.aspx");
        var content = await File.ReadAllTextAsync(page, ct);
        await File.WriteAllTextAsync(page, content.Replace("</form>", "    <p><%: Greeting() %></p>\n    </form>", StringComparison.Ordinal), ct);

        var snapshot = await Eventually.MatchesAsync(() => session.GetSnapshotAsync(wait: true, ct), s => s.Version > loaded.Version, ct);
        var after = await MarkupReferenceTests.MarkupHitsAsync(snapshot, copy.Root, "_Default.Greeting");

        Assert.Single(before);
        Assert.Equal(2, after.Count);
        Assert.Same(report, session.Report);
    }
}
