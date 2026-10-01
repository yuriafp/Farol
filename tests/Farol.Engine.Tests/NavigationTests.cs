using Farol.Engine.Navigation;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Farol.Engine.Tests;

/// <summary>Navigation over the legacy fixture: C# and VB.NET in classic .NET Framework projects.</summary>
[Collection(LegacyFixtureDefinition.Name)]
public sealed class NavigationTests(LegacyWorkspaceFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AC5_camel_case_search_finds_the_declaration_with_location_and_id()
    {
        Assert.SkipUnless(fixture.Available, "Needs Windows with Visual Studio or Build Tools.");

        var found = await DeclarationSearch.SearchAsync(fixture.Snapshot!, "OrdCalc", "any", project: null, Ct);

        var first = Assert.Single(found);
        Assert.Equal("T:Legacy.Core.Orders.OrderCalculator", first.Id);
        Assert.EndsWith("OrderCalculator.cs", SymbolFormatter.SourceLocation(first.Symbol)!.Value.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AC6_references_cross_projects_and_languages()
    {
        Assert.SkipUnless(fixture.Available, "Needs Windows with Visual Studio or Build Tools.");
        var candidate = await ResolveSingleAsync("OrderCalculator.GetTotal");

        var result = await ReferenceFinder.FindAsync(fixture.Snapshot!, candidate, Ct);

        Assert.Equal(
            ["Legacy.Desktop", "Legacy.VbLib", "Legacy.Web", "Legacy.Wpf"],
            result.References.Select(r => r.Project).Distinct().Order(StringComparer.Ordinal));
        Assert.All(result.References, r => Assert.Equal("call", r.Kind));
        Assert.Contains(result.References, r => r.FilePath.EndsWith(".vb", StringComparison.OrdinalIgnoreCase));
        Assert.Single(result.Definitions);
    }

    [Fact]
    public async Task AC9_an_ambiguous_name_returns_every_candidate()
    {
        Assert.SkipUnless(fixture.Available, "Needs Windows with Visual Studio or Build Tools.");

        var candidates = await SymbolLocator.ResolveAsync(fixture.Snapshot!, fixture.Root, "GetTotal", Ct);

        Assert.Equal(
            ["M:Legacy.Core.Orders.OrderCalculator.GetTotal(System.Int32)", "M:Legacy.Web.IOrderService.GetTotal(System.Int32)", "M:Legacy.Web.OrderService.GetTotal(System.Int32)"],
            candidates.Select(c => c.Id).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("M:Legacy.Core.Orders.OrderCalculator.GetTotal(System.Int32)")]
    [InlineData("M:Legacy.Core.Orders.OrderCalculator.GetTotal(System.Int32)~System.Decimal")]
    [InlineData("Legacy.Core.Orders.OrderCalculator.GetTotal")]
    [InlineData("Legacy.Core/Orders/OrderCalculator.cs:15")]
    public async Task Resolves_ids_qualified_names_and_positions_to_the_same_symbol(string reference)
    {
        Assert.SkipUnless(fixture.Available, "Needs Windows with Visual Studio or Build Tools.");

        var candidate = await ResolveSingleAsync(reference);

        Assert.Equal("M:Legacy.Core.Orders.OrderCalculator.GetTotal(System.Int32)", candidate.Id);
    }

    [Fact]
    public async Task AC10_interface_implementations_are_found()
    {
        Assert.SkipUnless(fixture.Available, "Needs Windows with Visual Studio or Build Tools.");
        var candidate = await ResolveSingleAsync("IOrderRepository");

        var entries = await HierarchyFinder.FindAsync(fixture.Snapshot!.Solution, candidate, "implementations", Ct);

        var entry = Assert.Single(entries);
        Assert.Equal("implementation", entry.Relation);
        Assert.Equal("InMemoryOrderRepository", entry.Symbol.Name);
    }

    [Fact]
    public async Task AC11_callers_include_csharp_and_vb_members()
    {
        Assert.SkipUnless(fixture.Available, "Needs Windows with Visual Studio or Build Tools.");
        var candidate = await ResolveSingleAsync("OrderCalculator.GetTotal");

        var callers = await CallHierarchyFinder.FindAsync(fixture.Snapshot!.Solution, candidate, "callers", depth: 1, Ct);

        Assert.Equal(
            ["GetOrderTotal", "GetTotal", "OnCalculateClick", "TotalWithShipping", "btnCalculate_Click", "calculateButton_Click"],
            callers.Select(c => c.Symbol.Name).Order(StringComparer.Ordinal));
        var vb = Assert.Single(callers, c => c.Symbol.Name == "TotalWithShipping");
        Assert.Equal(LanguageNames.VisualBasic, vb.Symbol.Language);
        Assert.All(callers, c => Assert.NotEmpty(c.Sites));
    }

    [Fact]
    public async Task Callees_follow_calls_and_constructions()
    {
        Assert.SkipUnless(fixture.Available, "Needs Windows with Visual Studio or Build Tools.");
        var candidate = await ResolveSingleAsync("LegacyService.GetOrderTotal");

        var callees = await CallHierarchyFinder.FindAsync(fixture.Snapshot!.Solution, candidate, "callees", depth: 1, Ct);

        Assert.Contains(callees, c => c.Symbol.Name == "GetTotal");
        Assert.Contains(callees, c => c.Symbol is IMethodSymbol { MethodKind: MethodKind.Constructor, ContainingType.Name: "OrderCalculator" });
    }

    [Fact]
    public async Task AC12_file_outline_lists_members_with_lines()
    {
        Assert.SkipUnless(fixture.Available, "Needs Windows with Visual Studio or Build Tools.");
        var path = Path.Combine(fixture.Root, "Legacy.Core", "Orders", "OrderCalculator.cs");
        var document = fixture.Snapshot!.Solution.GetDocument(fixture.Snapshot.Solution.GetDocumentIdsWithFilePath(path)[0])!;

        var outline = await OutlineBuilder.ForDocumentAsync(document, Ct);

        Assert.Equal(["OrderCalculator", "_repository", ".ctor", "GetTotal", "TaxRate"], outline.Select(e => e.Symbol.Name));
        Assert.Equal(0, outline[0].Depth);
        Assert.All(outline.Skip(1), e => Assert.Equal(1, e.Depth));
        Assert.Equal(15, Assert.Single(outline, e => e.Symbol.Name == "GetTotal").Line);
    }

    [Fact]
    public async Task VB_symbols_render_in_visual_basic_syntax()
    {
        Assert.SkipUnless(fixture.Available, "Needs Windows with Visual Studio or Build Tools.");
        var candidate = await ResolveSingleAsync("ShippingCalculator.TotalWithShipping");

        var display = SymbolFormatter.Display(candidate.Symbol);

        Assert.Contains("As Decimal", display, StringComparison.Ordinal);
        Assert.Contains("IEnumerable(Of Integer)", display, StringComparison.Ordinal);
    }

    private async Task<SymbolCandidate> ResolveSingleAsync(string reference) =>
        Assert.Single(await SymbolLocator.ResolveAsync(fixture.Snapshot!, fixture.Root, reference, Ct));
}
