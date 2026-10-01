using Farol.Engine.Workspaces;
using Farol.Testing;
using Xunit;

namespace Farol.Engine.Tests;

public sealed class ProjectFileInspectorTests
{
    [Fact]
    public void Reads_an_sdk_style_multi_targeted_project()
    {
        var facts = ProjectFileInspector.Inspect(Path.Combine(TestPaths.ModernDirectory, "src", "Modern.Core", "Modern.Core.csproj"));

        Assert.True(facts.IsSdkStyle);
        Assert.Equal("Microsoft.NET.Sdk", facts.Sdk);
        Assert.Equal(["net10.0", "net48"], facts.DeclaredTargetFrameworks);
        Assert.Equal("C#", facts.Language);
    }

    [Fact]
    public void Reads_a_classic_project()
    {
        var facts = ProjectFileInspector.Inspect(Path.Combine(TestPaths.LegacyDirectory, "Legacy.Core", "Legacy.Core.csproj"));

        Assert.False(facts.IsSdkStyle);
        Assert.Equal(["net48"], facts.DeclaredTargetFrameworks);
        Assert.Equal("Library", facts.OutputType);
    }

    [Fact]
    public void Recognizes_classic_project_flavors()
    {
        var web = ProjectFileInspector.Inspect(Path.Combine(TestPaths.LegacyDirectory, "Legacy.Web", "Legacy.Web.csproj"));
        var wpf = ProjectFileInspector.Inspect(Path.Combine(TestPaths.LegacyDirectory, "Legacy.Wpf", "Legacy.Wpf.csproj"));

        Assert.Contains(ProjectTypeGuids.WebApplication, web.ProjectTypeGuids);
        Assert.Contains(ProjectTypeGuids.Wpf, wpf.ProjectTypeGuids);
    }

    [Fact]
    public void Recognizes_visual_basic_projects()
    {
        var facts = ProjectFileInspector.Inspect(Path.Combine(TestPaths.LegacyDirectory, "Legacy.VbLib", "Legacy.VbLib.vbproj"));

        Assert.Equal("VB", facts.Language);
        Assert.False(facts.IsSdkStyle);
    }

    [Theory]
    [InlineData("v4.8", "net48")]
    [InlineData("v4.7.2", "net472")]
    [InlineData("v4.5.1", "net451")]
    [InlineData("v3.5", "net35")]
    public void Converts_classic_framework_versions_to_short_names(string version, string expected) =>
        Assert.Equal(expected, ProjectFileInspector.ToShortFrameworkName(version));
}
