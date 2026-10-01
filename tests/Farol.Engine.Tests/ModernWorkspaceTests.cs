using Farol.Engine.Analysis;
using Xunit;

namespace Farol.Engine.Tests;

[Collection(ModernFixtureDefinition.Name)]
public sealed class ModernWorkspaceTests(ModernWorkspaceFixture fixture)
{
    [Fact]
    public void Loads_every_project_with_its_target_frameworks()
    {
        var report = fixture.Session.Report!;

        Assert.Equal(3, report.ProjectFiles);
        Assert.Equal(4, report.RoslynProjects); // Modern.Core is loaded once per target framework.
        Assert.DoesNotContain(report.Issues, i => i.Severity == "error");
        var core = report.TargetFrameworks.Single(kv => kv.Key.EndsWith("Modern.Core.csproj", StringComparison.OrdinalIgnoreCase)).Value;
        Assert.Equal(["net10.0", "net48"], core);
    }

    [Fact]
    public void Restore_warnings_replayed_by_design_time_builds_are_warnings_with_their_code()
    {
        var issues = fixture.Session.Report!.Issues;

        // MSBuildWorkspace reports them as failures; the fixture pins vulnerable packages, so restore recorded NU1902/NU1903.
        var core = Assert.Single(issues, i => i.Message.StartsWith("NU1903 in ", StringComparison.Ordinal));
        Assert.Equal("warning", core.Severity);
        Assert.Contains("Modern.Core.csproj: ", core.Message, StringComparison.Ordinal);
        Assert.Contains("https://github.com/advisories/GHSA-8g4q-xg66-9fp4", core.Message, StringComparison.Ordinal);
        Assert.Equal(2, issues.Count(i => i.Message.StartsWith("NU1902 in ", StringComparison.Ordinal)));
    }

    [Fact]
    public void Overview_recognizes_aspnetcore_multitargeting_and_central_package_management()
    {
        var overview = SolutionOverviewBuilder.Build(fixture.Session.Target, fixture.Snapshot.Solution, fixture.Session.Report);

        Assert.True(overview.CentralPackageManagement);
        var api = Assert.Single(overview.Projects, p => p.Name == "Modern.Api");
        Assert.Contains("ASP.NET Core", api.AppModels);
        Assert.Equal(["Modern.Core"], api.ProjectReferences);
        var core = Assert.Single(overview.Projects, p => p.Name == "Modern.Core");
        Assert.True(core.IsSdkStyle);
        Assert.True(core.TargetsNetFramework);
        Assert.Equal("library", core.OutputKind);
    }

    [Fact]
    public void Catalog_maps_each_roslyn_project_to_its_file_and_target_framework()
    {
        var entries = fixture.Snapshot.Solution.ProjectIds.Select(id => fixture.Snapshot.Projects.Get(id)!).ToList();

        Assert.Equal(["net10.0", "net48"], entries.Where(e => e.Name == "Modern.Core").Select(e => e.TargetFramework).Order(StringComparer.Ordinal));
        Assert.Equal("net10.0", Assert.Single(entries, e => e.Name == "Modern.Api").TargetFramework);
        Assert.All(entries, e => Assert.True(e.IsSdkStyle));
    }
}
