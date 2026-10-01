using Farol.Engine.Navigation;
using Farol.Engine.Workspaces;
using Farol.Testing;
using Xunit;

namespace Farol.Engine.Tests;

/// <summary>Edits made on disk (by an agent or an editor) reach the next request without a manual reload.</summary>
public sealed class WorkspaceSyncTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Edited_source_is_visible_in_the_next_snapshot_without_a_reload()
    {
        using var copy = FixtureCopy.Create(TestPaths.ModernDirectory);
        await FixtureRestore.EnsureRestoredAsync(copy.PathOf("Modern.slnx"), Ct);
        await using var engine = EngineHarness.Create(copy.Root);
        var session = engine.Workspaces.GetSession(null);
        var loaded = await session.GetSnapshotAsync(wait: true, Ct);
        var report = session.Report;

        var file = copy.PathOf("src", "Modern.Core", "Pricing", "PriceCalculator.cs");
        await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file, Ct)).Replace("IsWeekend", "IsRestDay", StringComparison.Ordinal), Ct);

        var snapshot = await Eventually.MatchesAsync(
            () => session.GetSnapshotAsync(wait: true, Ct),
            s => s.Version > loaded.Version,
            Ct);
        var renamed = await DeclarationSearch.SearchAsync(snapshot, "IsRestDay", "method", project: null, Ct);
        var old = await DeclarationSearch.SearchAsync(snapshot, "IsWeekend", "method", project: null, Ct);

        Assert.Equal(2, Assert.Single(renamed).Variants.Count); // updated in both target frameworks
        Assert.Empty(old);
        Assert.Same(report, session.Report); // applied in place, not reloaded
    }

    [Fact]
    public async Task New_file_in_an_sdk_style_project_joins_the_project_without_a_reload()
    {
        using var copy = FixtureCopy.Create(TestPaths.ModernDirectory);
        await FixtureRestore.EnsureRestoredAsync(copy.PathOf("Modern.slnx"), Ct);
        await using var engine = EngineHarness.Create(copy.Root);
        var session = engine.Workspaces.GetSession(null);
        var loaded = await session.GetSnapshotAsync(wait: true, Ct);
        var report = session.Report;

        await File.WriteAllTextAsync(
            copy.PathOf("src", "Modern.Core", "Pricing", "TaxCalculator.cs"),
            "namespace Modern.Core.Pricing;\n\npublic sealed class TaxCalculator\n{\n    public decimal Rate => 0.1m;\n}\n",
            Ct);

        var snapshot = await Eventually.MatchesAsync(() => session.GetSnapshotAsync(wait: true, Ct), s => s.Version > loaded.Version, Ct);
        var found = await DeclarationSearch.SearchAsync(snapshot, "TaxCalculator", "class", project: null, Ct);

        Assert.Equal(2, Assert.Single(found).Variants.Count);
        Assert.Same(report, session.Report);
    }

    [Fact]
    public async Task Project_file_change_reloads_the_workspace()
    {
        using var copy = FixtureCopy.Create(TestPaths.ModernDirectory);
        await FixtureRestore.EnsureRestoredAsync(copy.PathOf("Modern.slnx"), Ct);
        await using var engine = EngineHarness.Create(copy.Root);
        var session = engine.Workspaces.GetSession(null);
        await session.GetSnapshotAsync(wait: true, Ct);
        var report = session.Report;

        var project = copy.PathOf("src", "Modern.Core", "Modern.Core.csproj");
        await File.WriteAllTextAsync(project, (await File.ReadAllTextAsync(project, Ct)).Replace("</Project>", "  <!-- touched -->\n</Project>", StringComparison.Ordinal), Ct);

        await Eventually.MatchesAsync(
            async () =>
            {
                await session.GetSnapshotAsync(wait: true, Ct);
                return session.Report;
            },
            r => !ReferenceEquals(r, report),
            Ct);

        Assert.NotSame(report, session.Report);
        Assert.Equal(WorkspaceState.Ready, session.State);
    }
}
