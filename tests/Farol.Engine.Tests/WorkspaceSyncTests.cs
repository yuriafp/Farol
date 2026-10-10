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
    public async Task A_file_put_back_as_it_was_returns_the_snapshot_to_the_loaded_solution()
    {
        using var copy = FixtureCopy.Create(TestPaths.ModernDirectory);
        await FixtureRestore.EnsureRestoredAsync(copy.PathOf("Modern.slnx"), Ct);
        await using var engine = EngineHarness.Create(copy.Root);
        var session = engine.Workspaces.GetSession(null);
        var loaded = await session.GetSnapshotAsync(wait: true, Ct);
        var file = copy.PathOf("src", "Modern.Core", "Pricing", "PriceCalculator.cs");
        var original = await File.ReadAllTextAsync(file, Ct);

        await File.WriteAllTextAsync(file, "// edited\n" + original, Ct);
        var edited = await Eventually.MatchesAsync(() => session.GetSnapshotAsync(wait: true, Ct), s => s.Version > loaded.Version, Ct);
        await File.WriteAllTextAsync(file, original, Ct);
        var reverted = await Eventually.MatchesAsync(() => session.GetSnapshotAsync(wait: true, Ct), s => s.Version > edited.Version, Ct);

        // The load's solution, compilations included: nothing has to be compiled again for an undone edit.
        Assert.NotSame(loaded.Solution, edited.Solution);
        Assert.Same(loaded.Solution, reverted.Solution);
    }

    [Fact]
    public async Task The_load_snapshot_keeps_the_text_files_had_when_they_loaded()
    {
        using var copy = FixtureCopy.Create(TestPaths.ModernDirectory);
        await FixtureRestore.EnsureRestoredAsync(copy.PathOf("Modern.slnx"), Ct);
        await using var engine = EngineHarness.Create(copy.Root);
        var session = engine.Workspaces.GetSession(null);
        var loaded = await session.GetSnapshotAsync(wait: true, Ct);
        var file = copy.PathOf("src", "Modern.Core", "Pricing", "PriceCalculator.cs");

        // Nothing has asked for this file's text yet: read lazily, it would come back already edited.
        await File.WriteAllTextAsync(file, "// edited after load\n" + await File.ReadAllTextAsync(file, Ct), Ct);
        var edited = await Eventually.MatchesAsync(() => session.GetSnapshotAsync(wait: true, Ct), s => s.Version > loaded.Version, Ct);
        var atLoad = await loaded.Solution.GetDocument(loaded.Solution.GetDocumentIdsWithFilePath(file)[0])!.GetTextAsync(Ct);

        Assert.True(edited.Version > loaded.Version);
        Assert.DoesNotContain("edited after load", atLoad.ToString(), StringComparison.Ordinal);
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
    public async Task An_edit_to_a_file_the_project_leaves_out_does_not_add_it()
    {
        using var copy = FixtureCopy.Create(TestPaths.ModernDirectory);
        var project = copy.PathOf("src", "Modern.Core", "Modern.Core.csproj");
        await File.WriteAllTextAsync(project, (await File.ReadAllTextAsync(project, Ct)).Replace("</Project>", "  <ItemGroup>\n    <Compile Remove=\"Drafts/**\" />\n  </ItemGroup>\n</Project>", StringComparison.Ordinal), Ct);
        var draft = copy.PathOf("src", "Modern.Core", "Drafts", "Draft.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(draft)!);
        await File.WriteAllTextAsync(draft, "namespace Modern.Core;\n\npublic static class Draft\n{\n}\n", Ct);
        await FixtureRestore.EnsureRestoredAsync(copy.PathOf("Modern.slnx"), Ct);
        await using var engine = EngineHarness.Create(copy.Root);
        var session = engine.Workspaces.GetSession(null);
        var loaded = await session.GetSnapshotAsync(wait: true, Ct);

        // The draft first, then a file the project compiles: once that edit shows, the draft's has been seen too.
        await File.WriteAllTextAsync(draft, "namespace Modern.Core;\n\npublic static class Draft\n{\n    public static int One() => \"one\";\n}\n", Ct);
        var file = copy.PathOf("src", "Modern.Core", "Pricing", "PriceCalculator.cs");
        await File.WriteAllTextAsync(file, "// edited\n" + await File.ReadAllTextAsync(file, Ct), Ct);
        var snapshot = await Eventually.MatchesAsync(() => session.GetSnapshotAsync(wait: true, Ct), s => s.Version > loaded.Version, Ct);

        Assert.True(snapshot.Version > loaded.Version);
        Assert.Empty(snapshot.Solution.GetDocumentIdsWithFilePath(draft));
    }

    [Fact]
    public async Task A_renamed_file_stays_in_its_project()
    {
        using var copy = FixtureCopy.Create(TestPaths.ModernDirectory);
        await FixtureRestore.EnsureRestoredAsync(copy.PathOf("Modern.slnx"), Ct);
        await using var engine = EngineHarness.Create(copy.Root);
        var session = engine.Workspaces.GetSession(null);
        await session.GetSnapshotAsync(wait: true, Ct);
        var file = copy.PathOf("src", "Modern.Core", "Pricing", "PriceCalculator.cs");
        var renamed = copy.PathOf("src", "Modern.Core", "Pricing", "Pricing.cs");

        // A rename keeps the file's creation time: it arrives all the same.
        File.Move(file, renamed);
        var snapshot = await Eventually.MatchesAsync(
            () => session.GetSnapshotAsync(wait: true, Ct), s => s.Solution.GetDocumentIdsWithFilePath(renamed).Length > 0, Ct);

        Assert.Equal(2, snapshot.Solution.GetDocumentIdsWithFilePath(renamed).Length); // in both target frameworks
        Assert.Empty(snapshot.Solution.GetDocumentIdsWithFilePath(file));
    }

    [Fact]
    public async Task A_new_file_its_writer_still_holds_joins_once_it_can_be_read()
    {
        using var copy = FixtureCopy.Create(TestPaths.ModernDirectory);
        await FixtureRestore.EnsureRestoredAsync(copy.PathOf("Modern.slnx"), Ct);
        await using var engine = EngineHarness.Create(copy.Root);
        var session = engine.Workspaces.GetSession(null);
        await session.GetSnapshotAsync(wait: true, Ct);
        var path = copy.PathOf("src", "Modern.Core", "Pricing", "TaxCalculator.cs");

        var held = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await held.WriteAsync("namespace Modern.Core.Pricing;\n\npublic sealed class TaxCalculator\n{\n}\n"u8.ToArray(), Ct);
        await held.FlushAsync(Ct);
        session.NotifyEdited([path]);

        // Long enough for the file-system events to arrive and fail to read too, while the writer holds the file.
        var whileHeld = await session.GetSnapshotAsync(wait: true, Ct);
        for (var deadline = DateTime.UtcNow.AddSeconds(1); DateTime.UtcNow < deadline;)
        {
            await Task.Delay(100, Ct);
            whileHeld = await session.GetSnapshotAsync(wait: true, Ct);
        }

        await held.DisposeAsync();
        var released = await Eventually.MatchesAsync(
            () => session.GetSnapshotAsync(wait: true, Ct), s => s.Solution.GetDocumentIdsWithFilePath(path).Length > 0, Ct, timeoutMs: 3_000);

        Assert.Empty(whileHeld.Solution.GetDocumentIdsWithFilePath(path));
        Assert.NotEmpty(released.Solution.GetDocumentIdsWithFilePath(path));
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
