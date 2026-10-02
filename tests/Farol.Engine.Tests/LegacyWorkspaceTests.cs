using Farol.Engine.Analysis;
using Farol.Testing;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Farol.Engine.Tests;

/// <summary>
/// Spike A: can MSBuildWorkspace (BuildHost-net472 + Visual Studio MSBuild) load classic .NET Framework
/// projects with full fidelity? Loading is asserted; compiler fidelity is recorded in artifacts/spikes.
/// </summary>
[Collection(LegacyFixtureDefinition.Name)]
public sealed class LegacyWorkspaceTests(LegacyWorkspaceFixture fixture)
{
    [Fact]
    public async Task AC1_loads_classic_projects_with_visual_studio_msbuild()
    {
        Assert.SkipUnless(fixture.Available, "Needs Windows with Visual Studio or Build Tools.");
        var solution = fixture.Snapshot!.Solution;
        await using var engine = EngineHarness.Create(fixture.Root);
        var session = engine.Workspaces.GetSession(null);
        await session.GetSnapshotAsync(wait: true, TestContext.Current.CancellationToken);

        await SpikeReport.WriteAsync("spike-a-msbuildworkspace", session.CurrentSolution!, session.Report!, TestContext.Current.CancellationToken);

        Assert.Equal(6, session.Report!.ProjectFiles);
        Assert.Empty(session.Report.Issues);
        Assert.Contains(solution.Projects, p => p.Language == LanguageNames.VisualBasic);
    }

    [Fact]
    public async Task AC3_overview_recognizes_every_legacy_app_model()
    {
        Assert.SkipUnless(fixture.Available, "Needs Windows with Visual Studio or Build Tools.");
        await using var engine = EngineHarness.Create(fixture.Root);
        var session = engine.Workspaces.GetSession(null);
        var solution = await session.GetSolutionAsync(wait: true, TestContext.Current.CancellationToken);
        var toolchain = await engine.Toolchain.ProbeAsync(fixture.Root, TestContext.Current.CancellationToken);
        var overview = SolutionOverviewBuilder.Build(session.Target, solution, session.Report);

        Assert.All(overview.Projects, p => Assert.False(p.IsSdkStyle));
        Assert.All(overview.Projects, p => Assert.Equal(["net48"], p.TargetFrameworks));
        var web = Assert.Single(overview.Projects, p => p.Name == "Legacy.Web");
        Assert.Equal(["WebForms", "ASMX", "WCF service"], web.AppModels);
        Assert.Contains("WinForms", Assert.Single(overview.Projects, p => p.Name == "Legacy.Desktop").AppModels);
        Assert.Contains("WPF", Assert.Single(overview.Projects, p => p.Name == "Legacy.Wpf").AppModels);
        Assert.Equal("VB", Assert.Single(overview.Projects, p => p.Name == "Legacy.VbLib").Language);
        Assert.EndsWith(Path.Combine("Legacy.Web", "Web.config"), Assert.Single(overview.BindingRedirectFiles), StringComparison.Ordinal);
        Assert.Contains("MSBuild.exe", BuildAdvisor.SuggestBuildCommand(overview, toolchain, fixture.Root), StringComparison.OrdinalIgnoreCase);
    }
}
