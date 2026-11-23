using Farol.Engine.Loading;
using Farol.Engine.Navigation;
using Farol.Engine.Workspaces;
using Farol.Testing;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Farol.Engine.Tests;

/// <summary>Spec 002, AC-54, AC-57: the heuristic loader reads classic projects without MSBuild, on any OS.</summary>
public sealed class HeuristicLoaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AC54_the_legacy_fixture_loads_heuristically_and_finds_the_cross_language_call_sites()
    {
        await using var engine = EngineHarness.Create(TestPaths.LegacyDirectory, configure: o => o.Loader = HeuristicWorkspaceLoader.Name);
        var session = engine.Workspaces.GetSession(null);
        var snapshot = await session.GetSnapshotAsync(wait: true, Ct);

        Assert.Equal(HeuristicWorkspaceLoader.Name, session.Report!.Loader);
        Assert.True(snapshot.Load.IsHeuristic);
        Assert.Equal(
            ["Legacy.Core", "Legacy.Desktop", "Legacy.Tests", "Legacy.VbLib", "Legacy.Web", "Legacy.Wpf"],
            snapshot.Solution.Projects.Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Contains(session.Report.Issues, i => i.Message.StartsWith("Heuristic load:", StringComparison.Ordinal));

        var candidate = Assert.Single(await SymbolLocator.ResolveAsync(snapshot, TestPaths.LegacyDirectory, "OrderCalculator.GetTotal", Ct));
        var result = await ReferenceFinder.FindAsync(snapshot, candidate, Ct);
        Assert.Equal(
            ["Legacy.Desktop", "Legacy.Tests", "Legacy.VbLib", "Legacy.Web", "Legacy.Wpf"],
            result.References.Select(r => r.Project).Distinct().Order(StringComparer.Ordinal));
        Assert.Contains(result.References, r => r.FilePath.EndsWith(".vb", StringComparison.OrdinalIgnoreCase));

        // Compiler errors of the heuristic load, project by project: what MSBuild targets would have added.
        var errors = new List<string>();
        foreach (var project in snapshot.Solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(Ct);
            errors.AddRange(compilation!.GetDiagnostics(Ct).Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => $"{project.Name}: {d}"));
        }

        // Only the WPF project's: XAML code-behind parts (InitializeComponent, named elements, App's Main) come from targets.
        Assert.DoesNotContain(errors, e => !e.StartsWith("Legacy.Wpf:", StringComparison.Ordinal));
        Assert.All(errors, e => Assert.Matches("CS5001|CS0103", e));
        Assert.Contains(session.Report.Issues, i => i.Message.StartsWith("Legacy.Wpf: XAML code-behind parts", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AC57_offline_without_reference_assemblies_in_the_cache_the_load_says_what_is_missing()
    {
        using var copy = FixtureCopy.Create(TestPaths.LegacyDirectory);
        var emptyCache = Directory.CreateTempSubdirectory("farol-empty-nuget-").FullName;
        try
        {
            await File.WriteAllTextAsync(copy.PathOf("nuget.config"), $"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <config><add key="globalPackagesFolder" value="{emptyCache}" /></config>
                  <fallbackPackageFolders><clear /></fallbackPackageFolders>
                </configuration>
                """, Ct);
            await using var engine = EngineHarness.Create(copy.Root, configure: o =>
            {
                o.Loader = HeuristicWorkspaceLoader.Name;
                o.Offline = true;
            });
            var session = engine.Workspaces.GetSession(null);
            await session.GetSnapshotAsync(wait: true, Ct);

            var missing = Assert.Single(session.Report!.Issues, i => i.Message.Contains("reference assemblies", StringComparison.Ordinal));
            Assert.Equal("error", missing.Severity);
            Assert.Contains("Microsoft.NETFramework.ReferenceAssemblies.net48", missing.Message, StringComparison.Ordinal);
            Assert.Contains("Farol runs offline", missing.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(emptyCache, recursive: true);
        }
    }
}
