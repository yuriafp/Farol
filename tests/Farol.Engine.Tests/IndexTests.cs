using Farol.Engine.Indexing;
using Farol.Engine.Navigation;
using Farol.Engine.Workspaces;
using Farol.Testing;
using Microsoft.CodeAnalysis;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Farol.Engine.Tests;

/// <summary>Spec 002, AC-50 – AC-52: the persistent index answers navigation as the load does, says what changed since, and keeps to its rules.</summary>
public sealed class IndexTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AC50_the_index_answers_every_symbol_of_the_legacy_fixture_as_the_load_does()
    {
        using var copy = FixtureCopy.Create(TestPaths.LegacyDirectory);
        using var indexes = new TemporaryDirectory();
        await using var engine = EngineHarness.Create(copy.Root, configure: o => Indexed(o, indexes.Path));
        var (session, snapshot) = await LoadAndIndexAsync(engine);
        var index = Assert.IsType<WorkspaceIndex>(engine.Workspaces.Indexes.Open(session.Target));

        foreach (var query in new[] { "OrderCalculator", "GetTotal", "OrdCalc", "Order", "Calc", "Shipping", "IOrder" })
        {
            Assert.Equal(
                (await DeclarationSearch.SearchAsync(snapshot, query, "any", null, Ct)).Select(c => c.Id),
                index.FindSymbols(query, "any", null).Select(s => s.Id));
        }

        Assert.Equal(
            (await DeclarationSearch.SearchAsync(snapshot, "Order", "class", "Legacy.Core", Ct)).Select(c => c.Id),
            index.FindSymbols("Order", "class", "Legacy.Core").Select(s => s.Id));

        // Depth 1 only: past a VB to C# call, the load sees the C# project as metadata (Roslyn hands VB a compiled copy)
        // and stops, while the index follows the C# method on.
        await AssertAnswersAsTheLoadAsync(snapshot, index, copy, callDepth: 1, Path.Combine("Legacy.Core", "Orders", "OrderCalculator.cs"), Path.Combine("Legacy.VbLib", "ShippingCalculator.vb"), Path.Combine("Legacy.Web", "Default.aspx.cs"));

        var overview = index.Overview(session.Target);
        Assert.NotNull(overview);
        Assert.Equal(6, overview.Projects.Count);
        Assert.Equal("heuristic", overview.Loader);
        Assert.Empty(index.Freshness().ChangedFiles);
        Assert.False(index.Freshness().ProjectsChanged);
    }

    [Fact]
    public async Task AC50_the_index_answers_every_symbol_of_the_multi_targeted_modern_fixture_as_the_load_does()
    {
        using var copy = FixtureCopy.Create(TestPaths.ModernDirectory);
        await FixtureRestore.EnsureRestoredAsync(copy.PathOf("Modern.slnx"), Ct);
        using var indexes = new TemporaryDirectory();
        await using var engine = EngineHarness.Create(copy.Root, configure: o =>
        {
            o.Index = true;
            o.IndexDirectory = indexes.Path;
        });
        var (session, snapshot) = await LoadAndIndexAsync(engine);
        var index = Assert.IsType<WorkspaceIndex>(engine.Workspaces.Indexes.Open(session.Target));

        await AssertAnswersAsTheLoadAsync(snapshot, index, copy, callDepth: 2, Path.Combine("src", "Modern.Core", "Pricing", "PriceCalculator.cs"));
        Assert.Contains(index.AllSymbols(), s => s.TargetFrameworks.Count == 2);
    }

    /// <summary>Every declaration the index holds: its references, definitions, hierarchy and calls as the finders give them over the load.</summary>
    private static async Task AssertAnswersAsTheLoadAsync(WorkspaceSnapshot snapshot, WorkspaceIndex index, FixtureCopy copy, int callDepth, params string[] outlined)
    {
        var differences = new List<string>();
        var symbols = index.AllSymbols();
        Assert.True(symbols.Count > 10, $"{symbols.Count} symbols");
        foreach (var indexed in symbols)
        {
            var candidates = await SymbolLocator.ResolveAsync(snapshot, copy.Root, indexed.Id, Ct);
            var candidate = Assert.Single(candidates, c => c.Id == indexed.Id);
            Compare($"frameworks of {indexed.Id}", candidate.Variants.Select(v => snapshot.Projects.Get(v.ProjectId)?.TargetFramework).OfType<string>().Distinct(), indexed.TargetFrameworks, differences);

            var expected = await ReferenceFinder.FindAsync(snapshot, candidate, Ct);
            var actual = index.References(indexed);
            Compare($"references of {indexed.Id}", expected.References.Select(Hit), actual.References.Select(Hit), differences);
            Compare($"definitions of {indexed.Id}", expected.Definitions.Select(d => $"{d.FilePath}:{d.Line}"), actual.Definitions.Select(d => $"{d.FilePath}:{d.Line}"), differences);
            Compare($"through of {indexed.Id}", expected.Through.Select(SymbolFormatter.Id), actual.Through.Select(t => t.Id), differences);

            Compare(
                $"hierarchy of {indexed.Id}",
                (await HierarchyFinder.FindAsync(snapshot.Solution, candidate, "all", Ct)).Select(e => $"{e.Relation} {SymbolFormatter.Id(e.Symbol.OriginalDefinition)}"),
                index.Hierarchy(indexed, "all").Select(e => $"{e.Relation} {e.Symbol.Id}"),
                differences);

            if (indexed.Match is "method" or "property")
            {
                foreach (var direction in new[] { "callers", "callees" })
                {
                    Compare(
                        $"{direction} of {indexed.Id}",
                        // VB reports an extension method called as one in its reduced form; the index keeps the method as declared.
                        (await CallHierarchyFinder.FindAsync(snapshot.Solution, candidate, direction, callDepth, Ct)).SelectMany(Flatten).Select(n => $"{n.Depth} {SymbolFormatter.Id(n.Node.Symbol is IMethodSymbol { ReducedFrom: { } reduced } ? reduced.OriginalDefinition : n.Node.Symbol)} {string.Join(",", n.Node.Sites.Select(s => s.Line))}"),
                        index.Calls(indexed, direction, callDepth).SelectMany(Flatten).Select(n => $"{n.Depth} {n.Node.Symbol.Id} {string.Join(",", n.Node.Sites.Select(s => s.Line))}"),
                        differences);
                }
            }
        }

        foreach (var file in outlined)
        {
            var document = snapshot.Solution.GetDocument(SourceDocuments.Find(snapshot, copy.PathOf(file), file)[0])!;
            Compare(
                $"outline of {file}",
                (await OutlineBuilder.ForDocumentAsync(document, Ct)).Select(e => $"{e.Line} {e.Depth} {SymbolFormatter.Id(e.Symbol)}"),
                index.OutlineOfFile(copy.PathOf(file)).Select(e => $"{e.Line} {e.Depth} {e.Symbol.Id}"),
                differences);
        }

        Assert.True(differences.Count == 0, string.Join("\n", differences));
    }

    private static IEnumerable<(int Depth, CallNode Node)> Flatten(CallNode node) =>
        node.Children.SelectMany(Flatten).Select(c => (c.Depth + 1, c.Node)).Prepend((0, node));

    private static IEnumerable<(int Depth, IndexedCallNode Node)> Flatten(IndexedCallNode node) =>
        node.Children.SelectMany(Flatten).Select(c => (c.Depth + 1, c.Node)).Prepend((0, node));

    [Fact]
    public async Task AC51_files_changed_since_the_index_are_listed_and_project_file_changes_say_the_projects_may_have_changed()
    {
        using var copy = FixtureCopy.Create(TestPaths.LegacyDirectory);
        using var indexes = new TemporaryDirectory();
        WorkspaceTarget target;
        await using (var engine = EngineHarness.Create(copy.Root, configure: o => Indexed(o, indexes.Path)))
        {
            target = (await LoadAndIndexAsync(engine)).Session.Target;
        }

        var source = copy.PathOf("Legacy.Core", "Orders", "OrderCalculator.cs");
        File.AppendAllText(source, "\n// changed\n");
        await using (var engine = EngineHarness.Create(copy.Root, configure: o => Indexed(o, indexes.Path)))
        {
            var freshness = engine.Workspaces.Indexes.Open(target)!.Freshness();
            Assert.Equal([source], freshness.ChangedFiles);
            Assert.True(freshness.IsChanged(source));
            Assert.False(freshness.ProjectsChanged);
        }

        File.SetLastWriteTimeUtc(copy.PathOf("Legacy.Core", "Legacy.Core.csproj"), DateTime.UtcNow.AddMinutes(1));
        await using (var engine = EngineHarness.Create(copy.Root, configure: o => Indexed(o, indexes.Path)))
        {
            Assert.True(engine.Workspaces.Indexes.Open(target)!.Freshness().ProjectsChanged);
        }
    }

    [Fact]
    public async Task AC52_with_the_index_off_none_is_written_or_read()
    {
        using var copy = FixtureCopy.Create(TestPaths.LegacyDirectory);
        using var indexes = new TemporaryDirectory();
        await using var engine = EngineHarness.Create(copy.Root, configure: o =>
        {
            Indexed(o, indexes.Path);
            o.Index = false;
        });
        var session = engine.Workspaces.GetSession(null);
        await session.GetSnapshotAsync(wait: true, Ct);
        await Task.Delay(TimeSpan.FromSeconds(3), Ct);

        Assert.Null(session.IndexWrittenAt);
        Assert.False(Directory.Exists(indexes.Path) && Directory.EnumerateFiles(indexes.Path, "*", SearchOption.AllDirectories).Any());
        Assert.Null(engine.Workspaces.Indexes.Open(session.Target));
    }

    [Fact]
    public async Task AC52_a_corrupt_index_or_one_of_another_format_is_ignored_and_deleted_and_an_unused_one_expires()
    {
        using var indexes = new TemporaryDirectory();
        var target = new WorkspaceTarget(Path.Combine(indexes.Path, "App.sln"), WorkspaceTargetKind.Solution);
        var options = new FarolEngineOptions { IndexDirectory = indexes.Path };

        // Corrupt.
        var store = Store(options);
        var path = store.PathOf(target);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "not a database", Ct);
        Assert.Null(store.Open(target));
        Assert.False(File.Exists(path));

        // A query that failed: a busy or locked index is kept, a corrupt one deleted.
        await File.WriteAllTextAsync(path, "an index", Ct);
        store.Discard(target, new SqliteException("database is locked", 5));
        Assert.True(File.Exists(path));
        store.Discard(target, new SqliteException("database disk image is malformed", 11));
        Assert.False(File.Exists(path));

        // Another format version.
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL); INSERT INTO meta VALUES ('format', '0'), ('written', '2026-01-01T00:00:00Z');";
            command.ExecuteNonQuery();
        }

        Assert.Null(Store(options).Open(target));
        Assert.False(File.Exists(path));

        // Unused for 30 days: deleted at the next start; a recent one stays.
        var old = Path.Combine(indexes.Path, "0123456789abcdef0123456789abcdef");
        var recent = Path.Combine(indexes.Path, "fedcba9876543210fedcba9876543210");
        foreach (var folder in new[] { old, recent })
        {
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, WorkspaceIndexes.FileName), string.Empty, Ct);
            await File.WriteAllTextAsync(Path.Combine(folder, "used"), string.Empty, Ct);
        }

        // Left by servers that stopped while writing: deleted after a day.
        var stale = Path.Combine(recent, $"{WorkspaceIndexes.FileName}.0000.tmp");
        var writing = Path.Combine(recent, $"{WorkspaceIndexes.FileName}.1111.tmp");
        await File.WriteAllTextAsync(stale, string.Empty, Ct);
        await File.WriteAllTextAsync(writing, string.Empty, Ct);
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));

        // Farol:IndexDirectory may point at a folder that holds other things: only what Farol writes is deleted.
        var other = Path.Combine(indexes.Path, "photos");
        Directory.CreateDirectory(other);
        await File.WriteAllTextAsync(Path.Combine(other, "used"), string.Empty, Ct);
        var kept = Path.Combine(old, "notes.txt");
        await File.WriteAllTextAsync(kept, string.Empty, Ct);

        foreach (var file in Directory.EnumerateFiles(old).Concat(Directory.EnumerateFiles(other)))
        {
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-31));
        }

        Assert.Null(Store(options).Open(target));
        Assert.False(File.Exists(Path.Combine(old, WorkspaceIndexes.FileName)));
        Assert.Equal([kept], Directory.EnumerateFiles(old));
        Assert.True(File.Exists(Path.Combine(recent, WorkspaceIndexes.FileName)));
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(writing));
        Assert.True(File.Exists(Path.Combine(other, "used")));

        Directory.Delete(old, recursive: true);
        Directory.CreateDirectory(old);
        await File.WriteAllTextAsync(Path.Combine(old, WorkspaceIndexes.FileName), string.Empty, Ct);
        File.SetLastWriteTimeUtc(Path.Combine(old, WorkspaceIndexes.FileName), DateTime.UtcNow.AddDays(-31));
        Assert.Null(Store(options).Open(target));
        Assert.False(Directory.Exists(old));
    }

    private static WorkspaceIndexes Store(FarolEngineOptions options) =>
        new(Microsoft.Extensions.Options.Options.Create(options), Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkspaceIndexes>.Instance);

    internal static void Indexed(FarolEngineOptions options, string directory)
    {
        options.Loader = "heuristic";
        options.Index = true;
        options.IndexDirectory = directory;
    }

    internal static async Task<(WorkspaceSession Session, WorkspaceSnapshot Snapshot)> LoadAndIndexAsync(EngineHarness engine)
    {
        var session = engine.Workspaces.GetSession(null);
        var snapshot = await session.GetSnapshotAsync(wait: true, Ct);
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (session.IndexWrittenAt is null)
        {
            Assert.True(DateTime.UtcNow < deadline, "The index was not written within 2 minutes.");
            await Task.Delay(100, Ct);
        }

        return (session, snapshot);
    }

    private static string Hit(SourceHit h) => $"{h.Project} {h.FilePath}:{h.Line}:{h.Column} {h.Kind} via {h.Via} [{string.Join(",", h.TargetFrameworks)}] {h.Snippet}";

    private static string Hit(IndexedHit h) => $"{h.Project} {h.FilePath}:{h.Line}:{h.Column} {h.Kind} via {h.Via} [{string.Join(",", h.TargetFrameworks)}] {h.Snippet}";

    private static void Compare(string what, IEnumerable<string> expected, IEnumerable<string> actual, List<string> differences)
    {
        var e = expected.Order(StringComparer.Ordinal).ToList();
        var a = actual.Order(StringComparer.Ordinal).ToList();
        if (!e.SequenceEqual(a))
        {
            differences.Add($"{what}:\n  load only: {string.Join("\n    ", e.Except(a))}\n  index only: {string.Join("\n    ", a.Except(e))}");
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("farol-index-").FullName;

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
