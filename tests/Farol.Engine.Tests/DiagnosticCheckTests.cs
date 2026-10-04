using Farol.Engine.Diagnostics;
using Farol.Engine.Workspaces;
using Farol.Testing;
using Xunit;

namespace Farol.Engine.Tests;

/// <summary>The dotnet_check engine on copies of the fixtures: every test edits files after the load.</summary>
public sealed class DiagnosticCheckTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_load_baseline_survives_line_shifts()
    {
        await using var legacy = await LegacyCopy.LoadAsync();
        Assert.SkipUnless(legacy.Available, "Needs Windows with Visual Studio or Build Tools.");
        var session = legacy.Session!;

        var atLoad = await DiagnosticCheck.RunAsync(
            await session.GetSnapshotAsync(wait: true, Ct), new CheckRequest(CheckScope.Project, Project: "Legacy.Core", IncludeExisting: true), Ct);
        var serializer = legacy.Copy.PathOf("Legacy.Core", "Serialization", "LegacySerializer.cs");
        var shifted = await EditAsync(session, serializer, ("        // The swallowed", "        // One line more above the warning.\n        // The swallowed"));
        var bodyOnly = await DiagnosticCheck.RunAsync(shifted, new CheckRequest(CheckScope.Changed), Ct);
        var withExisting = await DiagnosticCheck.RunAsync(shifted, new CheckRequest(CheckScope.Changed, IncludeExisting: true), Ct);

        Assert.Empty(atLoad.New);
        var warning = Assert.Single(atLoad.Existing);
        Assert.Equal(("CS0168", 29), (warning.Id, warning.Line));

        Assert.Empty(bodyOnly.New);
        Assert.Empty(bodyOnly.CheckedProjects);
        Assert.Equal(serializer, Assert.Single(bodyOnly.CheckedFiles), ignoreCase: true);

        Assert.Empty(withExisting.New);
        Assert.Equal(["Legacy.Core"], withExisting.CheckedProjects);
        Assert.Equal(30, Assert.Single(withExisting.Existing).Line);
    }

    [Fact]
    public async Task A_body_edit_checks_its_file_and_a_signature_change_checks_the_files_that_use_it()
    {
        await using var legacy = await LegacyCopy.LoadAsync();
        Assert.SkipUnless(legacy.Available, "Needs Windows with Visual Studio or Build Tools.");
        var session = legacy.Session!;
        var calculator = legacy.Copy.PathOf("Legacy.Core", "Orders", "OrderCalculator.cs");

        var withBodyError = await EditAsync(session, calculator, ("return order.Subtotal() * (1 + TaxRate());", "return \"total\";"));
        var bodyCheck = await DiagnosticCheck.RunAsync(withBodyError, new CheckRequest(CheckScope.Changed), Ct);
        var withNewSignature = await EditAsync(
            session,
            calculator,
            ("return \"total\";", "return order.Subtotal() * (1 + TaxRate());"),
            ("public decimal GetTotal(int orderId)", "public decimal GetTotal(int orderId, bool includeTax)"));
        var signatureCheck = await DiagnosticCheck.RunAsync(withNewSignature, new CheckRequest(CheckScope.Changed), Ct);

        Assert.True(bodyCheck.New.Count == 1, Describe(bodyCheck));
        var error = bodyCheck.New[0];
        Assert.Equal(("CS0029", 23, "Legacy.Core"), (error.Id, error.Line, error.Project));
        Assert.Empty(bodyCheck.CheckedProjects);

        Assert.Contains(signatureCheck.New, d => d.Project == "Legacy.VbLib" && d.Line == 13 && d.Id.StartsWith("BC", StringComparison.Ordinal));
        Assert.Contains(signatureCheck.New, d => d.Project == "Legacy.Web" && d.Id == "CS7036");
        Assert.True(signatureCheck.CheckedProjects.Count == 0, Describe(signatureCheck));
        Assert.Contains(signatureCheck.CheckedFiles, f => f.EndsWith("ShippingCalculator.vb", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(signatureCheck.New, d => d.Id == "CS0168");
    }

    [Fact]
    public async Task An_interface_member_added_checks_its_implementations_and_a_new_method_checks_only_its_file()
    {
        await using var legacy = await LegacyCopy.LoadAsync();
        Assert.SkipUnless(legacy.Available, "Needs Windows with Visual Studio or Build Tools.");
        var session = legacy.Session!;
        var calculator = legacy.Copy.PathOf("Legacy.Core", "Orders", "OrderCalculator.cs");
        var repository = legacy.Copy.PathOf("Legacy.Core", "Orders", "IOrderRepository.cs");

        var withMethod = await EditAsync(session, calculator, ("public decimal GetTotal(int orderId)", "public int Discount() { return 0; }\n\n        public decimal GetTotal(int orderId)"));
        var methodCheck = await DiagnosticCheck.RunAsync(withMethod, new CheckRequest(CheckScope.Changed), Ct);
        var withMember = await EditAsync(session, repository, ("void Save(Order order);", "void Save(Order order);\n\n        void Delete(int id);"));
        var memberCheck = await DiagnosticCheck.RunAsync(withMember, new CheckRequest(CheckScope.Changed), Ct);

        // Nothing calls Discount yet: no other file can be broken by it, and no project is compiled whole.
        Assert.True(methodCheck.New.Count == 0 && methodCheck.CheckedProjects.Count == 0, Describe(methodCheck));
        Assert.Equal(calculator, Assert.Single(methodCheck.CheckedFiles), ignoreCase: true);

        var missing = Assert.Single(memberCheck.New);
        Assert.Equal(("CS0535", "Legacy.Core"), (missing.Id, missing.Project));
        Assert.EndsWith("InMemoryOrderRepository.cs", missing.FilePath, StringComparison.OrdinalIgnoreCase);
        Assert.True(memberCheck.CheckedProjects.Count == 0, Describe(memberCheck));
    }

    [Fact]
    public async Task An_error_in_one_target_framework_lists_that_framework()
    {
        using var copy = FixtureCopy.Create(TestPaths.ModernDirectory);
        await FixtureRestore.EnsureRestoredAsync(copy.PathOf("Modern.slnx"), Ct);
        await using var engine = EngineHarness.Create(copy.Root);
        var session = engine.Workspaces.GetSession(null);
        await session.GetSnapshotAsync(wait: true, Ct);

        // ArgumentNullException.ThrowIfNull exists since .NET 6, not in .NET Framework 4.8.
        var edited = await EditAsync(
            session,
            copy.PathOf("src", "Modern.Core", "Pricing", "PriceCalculator.cs"),
            ("        var subtotal = prices.Sum();", "        ArgumentNullException.ThrowIfNull(prices);\n        var subtotal = prices.Sum();"));
        var check = await DiagnosticCheck.RunAsync(edited, new CheckRequest(CheckScope.Changed), Ct);

        Assert.True(check.New.Count == 1, Describe(check));
        var error = check.New[0];
        Assert.Equal(("CS0117", 17), (error.Id, error.Line));
        Assert.Equal(["net48"], error.TargetFrameworks);
    }

    private static string Describe(CheckResult result) =>
        $"new: [{string.Join("; ", result.New.Select(d => $"{d.Id}@{d.Line}"))}] existing: [{string.Join("; ", result.Existing.Select(d => $"{d.Id}@{d.Line}"))}] " +
        $"changed: [{string.Join("; ", result.ChangedFiles.Select(Path.GetFileName))}] projects: [{string.Join("; ", result.CheckedProjects)}] files: [{string.Join("; ", result.CheckedFiles.Select(Path.GetFileName))}]";

    /// <summary>Writes the replacements in one go and returns the first snapshot that has the new content.</summary>
    internal static async Task<WorkspaceSnapshot> EditAsync(WorkspaceSession session, string path, params (string Old, string New)[] replacements)
    {
        var content = await File.ReadAllTextAsync(path, Ct);
        foreach (var (old, replacement) in replacements)
        {
            Assert.Contains(old, content, StringComparison.Ordinal);
            content = content.Replace(old, replacement, StringComparison.Ordinal);
        }

        await File.WriteAllTextAsync(path, content, Ct);
        var (snapshot, text) = await Eventually.MatchesAsync(
            async () =>
            {
                var current = await session.GetSnapshotAsync(wait: true, Ct);
                var ids = current.Solution.GetDocumentIdsWithFilePath(path);
                return (current, ids.IsEmpty ? null : (await current.Solution.GetDocument(ids[0])!.GetTextAsync(Ct)).ToString());
            },
            r => r.Item2 == content,
            Ct);
        Assert.Equal(content, text);
        return snapshot;
    }

    /// <summary>A loaded copy of the legacy fixture, or an unavailable one without Windows and Visual Studio.</summary>
    internal sealed class LegacyCopy : IAsyncDisposable
    {
        private LegacyCopy(FixtureCopy copy, EngineHarness engine, WorkspaceSession? session)
        {
            Copy = copy;
            Engine = engine;
            Session = session;
        }

        public FixtureCopy Copy { get; }

        public EngineHarness Engine { get; }

        public WorkspaceSession? Session { get; }

        public bool Available => Session is not null;

        public static async Task<LegacyCopy> LoadAsync()
        {
            var copy = FixtureCopy.Create(TestPaths.LegacyDirectory);
            var engine = EngineHarness.Create(copy.Root);
            if (!OperatingSystem.IsWindows() || (await engine.Toolchain.ProbeAsync(copy.Root, Ct)).PreferredVisualStudio is null)
            {
                return new LegacyCopy(copy, engine, null);
            }

            var session = engine.Workspaces.GetSession(null);
            await session.GetSnapshotAsync(wait: true, Ct);
            return new LegacyCopy(copy, engine, session);
        }

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            Copy.Dispose();
        }
    }
}
