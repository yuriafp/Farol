using Farol.Engine.Navigation;
using Farol.Engine.Testing;
using Farol.Engine.Workspaces;
using Xunit;

namespace Farol.Engine.Tests;

/// <summary>Test projects, test methods and the tests a symbol affects, on the modern fixture (xUnit v3).</summary>
[Collection(ModernFixtureDefinition.Name)]
public sealed class ModernAffectedTestsTests(ModernWorkspaceFixture fixture)
{
    [Fact]
    public async Task Finds_the_xunit_project_and_its_test_methods()
    {
        var project = Assert.Single(TestProjects.Find(fixture.Snapshot));
        var names = await TestProjects.TestNamesAsync(fixture.Snapshot.Solution, project, TestContext.Current.CancellationToken);

        Assert.Equal(("Modern.Tests", true, TestFrameworks.XUnit), (project.Name, project.IsSdkStyle, Assert.Single(project.Frameworks)));
        Assert.Equal(4, names.Count);
        Assert.Contains("Modern.Tests.PriceCalculatorTests.Total_rounds_to_cents", names);
    }

    [Theory]
    [InlineData("PriceCalculator.Total", 3)]
    [InlineData("PriceCalculator.IsWeekend", 3)]
    // Through IClock.UtcNow, PriceCalculator may call SystemClock too: its tests are selected as well.
    [InlineData("SystemClock.UtcNow", 4)]
    public async Task Tests_reaching_a_symbol_directly_or_through_callers(string symbol, int expected)
    {
        var tests = await AffectedAsync(fixture.Snapshot, fixture.Root, symbol);

        Assert.Equal(expected, tests.Count);
        Assert.DoesNotContain(tests, t => symbol != "SystemClock.UtcNow" && t.Contains("SystemClock", StringComparison.Ordinal));
    }

    internal static async Task<IReadOnlyList<string>> AffectedAsync(WorkspaceSnapshot snapshot, string root, string symbol)
    {
        var ct = TestContext.Current.CancellationToken;
        var candidate = Assert.Single(await SymbolLocator.ResolveAsync(snapshot, root, symbol, ct));
        var found = await AffectedTestFinder.FindAsync(snapshot, TestProjects.Find(snapshot), candidate.Variants, ct);
        Assert.True(found.Complete);
        return [.. found.TestsByProject.Values.SelectMany(t => t)];
    }
}

/// <summary>The legacy MSTest project, found and searched before its packages.config is restored.</summary>
[Collection(LegacyFixtureDefinition.Name)]
public sealed class LegacyAffectedTestsTests(LegacyWorkspaceFixture fixture)
{
    [Fact]
    public void Classic_mstest_project_is_found_without_a_restore()
    {
        Assert.SkipUnless(fixture.Available, "Needs Windows with Visual Studio or Build Tools.");

        var project = Assert.Single(TestProjects.Find(fixture.Snapshot!));

        Assert.Equal(("Legacy.Tests", false, TestFrameworks.MSTest), (project.Name, project.IsSdkStyle, Assert.Single(project.Frameworks)));
    }

    [Theory]
    [InlineData("OrderCalculator.GetTotal", new[] { "GetTotal_adds_the_default_tax", "GetTotal_rejects_unknown_orders" })]
    [InlineData("InMemoryOrderRepository.Find", new[] { "GetTotal_adds_the_default_tax", "GetTotal_rejects_unknown_orders", "Find_returns_the_saved_order" })]
    public async Task Tests_reaching_a_symbol_including_calls_through_its_interface(string symbol, string[] expected)
    {
        Assert.SkipUnless(fixture.Available, "Needs Windows with Visual Studio or Build Tools.");

        var tests = await ModernAffectedTestsTests.AffectedAsync(fixture.Snapshot!, fixture.Root, symbol);

        Assert.Equal(expected.Order(StringComparer.Ordinal), tests.Select(t => t[(t.LastIndexOf('.') + 1)..]).Order(StringComparer.Ordinal));
    }
}
