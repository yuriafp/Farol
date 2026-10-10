using Farol.Core;
using Farol.Engine.Navigation;
using Xunit;

namespace Farol.Engine.Tests;

[Collection(ModernFixtureDefinition.Name)]
public sealed class ModernNavigationTests(ModernWorkspaceFixture fixture)
{
    [Theory]
    [InlineData("Text.StringBuilder", "T:System.Text.StringBuilder")]
    [InlineData("StringBuilder.Capacity", "P:System.Text.StringBuilder.Capacity")]
    public async Task AC43_framework_names_resolve_once_across_target_frameworks(string reference, string id)
    {
        var candidate = Assert.Single(await SymbolLocator.ResolveAsync(fixture.Snapshot, fixture.Root, reference, TestContext.Current.CancellationToken));

        Assert.Equal(id, candidate.Id);
    }

    [Theory]
    [InlineData("Nope.StringBuilder")]
    [InlineData("Nope.Capacity")]
    public async Task A_qualifier_that_names_no_container_finds_nothing(string reference) =>
        Assert.Empty(await SymbolLocator.ResolveAsync(fixture.Snapshot, fixture.Root, reference, TestContext.Current.CancellationToken));

    [Fact]
    public async Task A_path_on_a_drive_named_like_an_id_prefix_is_a_position()
    {
        string? asked = null;

        await Assert.ThrowsAsync<FarolException>(() => SymbolLocator.ResolveAsync(
            fixture.Snapshot,
            path =>
            {
                asked = path;
                throw new FarolException(ErrorCodes.PathNotTrusted, "Refused.");
            },
            @"F:\src\A.cs:12",
            TestContext.Current.CancellationToken));

        Assert.Equal(@"F:\src\A.cs", asked);
    }

    [Fact]
    public async Task AC13_references_found_in_both_target_frameworks_appear_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var candidate = Assert.Single(await SymbolLocator.ResolveAsync(fixture.Snapshot, fixture.Root, "IClock", ct));
        Assert.Equal(2, candidate.Variants.Count); // net10.0 and net48 builds of Modern.Core

        var result = await ReferenceFinder.FindAsync(fixture.Snapshot, candidate, ct);

        var inCore = result.References.Where(r => r.FilePath.EndsWith("PriceCalculator.cs", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(inCore);
        Assert.All(inCore, r => Assert.Equal(["net10.0", "net48"], r.TargetFrameworks));
        var inApi = Assert.Single(result.References, r => r.FilePath.EndsWith("Program.cs", StringComparison.Ordinal));
        Assert.Equal(["net10.0"], inApi.TargetFrameworks);
        var inTests = Assert.Single(result.References, r => r.FilePath.EndsWith("PriceCalculatorTests.cs", StringComparison.Ordinal));
        Assert.Equal(["net10.0"], inTests.TargetFrameworks);
        Assert.Equal(inCore.Count + 2, result.References.Count);
    }

    [Fact]
    public async Task Symbols_in_multi_targeted_projects_carry_every_variant()
    {
        var ct = TestContext.Current.CancellationToken;

        var found = await DeclarationSearch.SearchAsync(fixture.Snapshot, "PriceCalculator", "class", project: null, ct);

        var calculator = Assert.Single(found, c => c.Id == "T:Modern.Core.Pricing.PriceCalculator");
        Assert.Equal(
            ["net10.0", "net48"],
            calculator.Variants.Select(v => fixture.Snapshot.Projects.Get(v.ProjectId)!.TargetFramework).Order(StringComparer.Ordinal));
    }
}
