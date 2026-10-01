using Farol.Core;
using Farol.Engine.Workspaces;
using Farol.Testing;
using Xunit;

namespace Farol.Engine.Tests;

public sealed class WorkspaceDiscoveryTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("farol-discovery-").FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    [Fact]
    public void Finds_the_solution_in_a_directory()
    {
        var target = WorkspaceDiscovery.Resolve(null, TestPaths.ModernDirectory);

        Assert.Equal(TestPaths.ModernSolution, target.Path, ignoreCase: true);
        Assert.Equal(WorkspaceTargetKind.Solution, target.Kind);
    }

    [Fact]
    public void Accepts_a_project_file_relative_to_the_root()
    {
        var target = WorkspaceDiscovery.Resolve("src/Modern.Core/Modern.Core.csproj", TestPaths.ModernDirectory);

        Assert.Equal(WorkspaceTargetKind.Project, target.Kind);
        Assert.EndsWith("Modern.Core.csproj", target.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Prefers_slnx_when_the_same_solution_exists_in_both_formats()
    {
        File.WriteAllText(Path.Combine(_temp, "App.sln"), string.Empty);
        File.WriteAllText(Path.Combine(_temp, "App.slnx"), "<Solution />");

        var target = WorkspaceDiscovery.Resolve(null, _temp);

        Assert.EndsWith("App.slnx", target.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Lists_every_candidate_when_ambiguous()
    {
        File.WriteAllText(Path.Combine(_temp, "Alpha.sln"), string.Empty);
        File.WriteAllText(Path.Combine(_temp, "Beta.sln"), string.Empty);

        var error = Assert.Throws<FarolException>(() => WorkspaceDiscovery.Resolve(null, _temp));

        Assert.Equal(ErrorCodes.WorkspaceAmbiguous, error.Code);
        Assert.Contains("Alpha.sln", error.Message, StringComparison.Ordinal);
        Assert.Contains("Beta.sln", error.Message, StringComparison.Ordinal);
        Assert.NotNull(error.Hint);
    }

    [Fact]
    public void Finds_a_solution_nested_under_src()
    {
        var src = Directory.CreateDirectory(Path.Combine(_temp, "src")).FullName;
        File.WriteAllText(Path.Combine(src, "Nested.sln"), string.Empty);

        var target = WorkspaceDiscovery.Resolve(null, _temp);

        Assert.EndsWith("Nested.sln", target.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reports_a_missing_workspace_with_a_hint()
    {
        var error = Assert.Throws<FarolException>(() => WorkspaceDiscovery.Resolve("missing/App.sln", _temp));

        Assert.Equal(ErrorCodes.WorkspaceNotFound, error.Code);
        Assert.NotNull(error.Hint);
    }
}
