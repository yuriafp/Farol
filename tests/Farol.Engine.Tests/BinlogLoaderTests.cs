using Farol.Core.Execution;
using Farol.Engine.Loading;
using Farol.Engine.Navigation;
using Farol.Testing;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Farol.Engine.Tests;

/// <summary>Spec 002, AC-56: a workspace loads from the binary log of a build, named as the workspace or as Farol:Binlog.</summary>
public sealed class BinlogLoaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AC56_the_legacy_fixture_loads_from_its_binary_log_and_says_when_the_log_is_older_than_the_sources()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Building classic .NET Framework projects needs Windows.");
        string msbuild;
        await using (var probe = EngineHarness.Create(TestPaths.LegacyDirectory))
        {
            var toolchain = await probe.Toolchain.ProbeAsync(TestPaths.LegacyDirectory, Ct);
            Assert.SkipWhen(toolchain.PreferredVisualStudio?.MSBuildPath is null, "Visual Studio or Build Tools MSBuild is not installed.");
            msbuild = toolchain.PreferredVisualStudio!.MSBuildPath!;
        }

        // A copy: the build writes bin/obj. Rebuild: an up-to-date build skips the compiler and the log records no call.
        using var copy = FixtureCopy.Create(TestPaths.LegacyDirectory);
        var build = await new LocalProcessRunner().RunAsync(
            new ProcessSpec(msbuild, [copy.PathOf("Legacy.sln"), "-restore", "-p:RestorePackagesConfig=true", "-t:Rebuild", "-nodeReuse:false", "-v:quiet", "-nologo", "-bl:legacy.binlog"], copy.Root, TimeSpan.FromMinutes(5)),
            Ct);
        Assert.True(build.ExitCode == 0, build.StandardOutput + build.StandardError);

        await using (var engine = EngineHarness.Create(copy.Root))
        {
            var session = engine.Workspaces.GetSession("legacy.binlog");
            var snapshot = await session.GetSnapshotAsync(wait: true, Ct);

            Assert.Equal(BinlogWorkspaceLoader.Name, session.Report!.Loader);
            Assert.True(snapshot.Load.IsBinaryLog);
            var load = Assert.Single(session.Report.Issues, i => i.Message.StartsWith("Binary-log load:", StringComparison.Ordinal));
            Assert.DoesNotContain("older than", load.Message, StringComparison.Ordinal);
            Assert.Equal(
                ["Legacy.Core", "Legacy.Desktop", "Legacy.Tests", "Legacy.VbLib", "Legacy.Web", "Legacy.Wpf"],
                snapshot.Solution.Projects.Select(p => p.AssemblyName).Order(StringComparer.Ordinal));

            var candidate = Assert.Single(await SymbolLocator.ResolveAsync(snapshot, copy.Root, "OrderCalculator.GetTotal", Ct));
            var result = await ReferenceFinder.FindAsync(snapshot, candidate, Ct);
            Assert.Contains(result.References, r => r.FilePath.EndsWith(".vb", StringComparison.OrdinalIgnoreCase));
            Assert.True(result.References.Select(r => r.Project).Distinct().Count() >= 4);

            // The exact compilations the build ran: no compiler error anywhere, VB (whose core library comes from /sdkpath) included.
            foreach (var project in snapshot.Solution.Projects)
            {
                var compilation = await project.GetCompilationAsync(Ct);
                Assert.Empty(compilation!.GetDiagnostics(Ct).Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => $"{project.Name}: {d}"));
            }
        }

        // A source changed after the build: the load still works and says the log is older.
        File.SetLastWriteTimeUtc(copy.PathOf(Path.Combine("Legacy.Core", "Orders", "OrderCalculator.cs")), DateTime.UtcNow.AddMinutes(5));
        await using (var engine = EngineHarness.Create(copy.Root, configure: o => o.Binlog = "legacy.binlog"))
        {
            var session = engine.Workspaces.GetSession(null);
            await session.GetSnapshotAsync(wait: true, Ct);

            Assert.Equal(BinlogWorkspaceLoader.Name, session.Report!.Loader);
            Assert.Contains(session.Report.Issues, i => i.Message.Contains("older than 1 source file(s)", StringComparison.Ordinal));
        }
    }
}
