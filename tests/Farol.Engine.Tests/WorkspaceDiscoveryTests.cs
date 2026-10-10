using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
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
    public async Task AC41_without_workspace_a_root_with_several_means_the_one_in_use()
    {
        File.WriteAllText(Path.Combine(_temp, "Alpha.sln"), string.Empty);
        File.WriteAllText(Path.Combine(_temp, "Beta.sln"), string.Empty);
        await using var engine = EngineHarness.Create(_temp);
        var workspaces = engine.Workspaces;

        var noneInUse = Assert.Throws<FarolException>(() => workspaces.GetSession(null));
        var alpha = workspaces.GetSession("Alpha.sln");
        var meant = workspaces.GetSession(null);
        var folder = workspaces.TargetDirectory(null);
        workspaces.GetSession("Beta.sln");
        var twoInUse = Assert.Throws<FarolException>(() => workspaces.GetSession(null));

        Assert.Equal(ErrorCodes.WorkspaceAmbiguous, noneInUse.Code);
        Assert.Same(alpha, meant);
        Assert.Equal(_temp, folder, ignoreCase: true);
        Assert.Equal(ErrorCodes.WorkspaceAmbiguous, twoInUse.Code);
        Assert.Contains("2 are in use: Alpha.sln, Beta.sln.", twoInUse.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AC41_without_workspace_a_root_with_none_means_the_one_in_use()
    {
        var deep = Directory.CreateDirectory(Path.Combine(_temp, "a", "b", "c")).FullName;
        File.WriteAllText(Path.Combine(deep, "App.slnx"), "<Solution />");
        await using var engine = EngineHarness.Create(_temp);
        var workspaces = engine.Workspaces;

        var noneInUse = Assert.Throws<FarolException>(() => workspaces.GetSession(null));
        var app = workspaces.GetSession("a/b/c/App.slnx");

        Assert.Equal(ErrorCodes.WorkspaceNotFound, noneInUse.Code);
        Assert.Same(app, workspaces.GetSession(null));
    }

    [Fact]
    public async Task AC41_the_solution_discovered_in_the_root_wins_over_the_one_in_use()
    {
        File.WriteAllText(Path.Combine(_temp, "App.slnx"), "<Solution />");
        Directory.CreateDirectory(Path.Combine(_temp, "other"));
        File.WriteAllText(Path.Combine(_temp, "other", "Other.slnx"), "<Solution />");
        await using var engine = EngineHarness.Create(_temp);
        var workspaces = engine.Workspaces;

        workspaces.GetSession("other/Other.slnx");
        var meant = workspaces.GetSession(null);

        Assert.Equal(Path.Combine(_temp, "App.slnx"), meant.Target.Path, ignoreCase: true);
    }

    [Fact]
    public void Finds_a_solution_nested_under_src()
    {
        var src = Directory.CreateDirectory(Path.Combine(_temp, "src")).FullName;
        File.WriteAllText(Path.Combine(src, "Nested.sln"), string.Empty);

        var target = WorkspaceDiscovery.Resolve(null, _temp);

        Assert.EndsWith("Nested.sln", target.Path, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A root such as a drive or a user profile has folders nobody may list ("System Volume Information", "Application Data").</summary>
    [Fact]
    public void Discovery_passes_over_folders_it_may_not_list()
    {
        var locked = Directory.CreateDirectory(Path.Combine(_temp, "Locked")).FullName;
        Directory.CreateDirectory(Path.Combine(_temp, "repo"));
        File.WriteAllText(Path.Combine(_temp, "repo", "App.slnx"), "<Solution />");
        using var denied = DenyListing(locked);
        Assert.Throws<UnauthorizedAccessException>(() => Directory.EnumerateFiles(locked).ToList());

        var target = WorkspaceDiscovery.Resolve(null, _temp);

        Assert.Equal(Path.Combine(_temp, "repo", "App.slnx"), target.Path, ignoreCase: true);
    }

    [Fact]
    public void Reports_a_missing_workspace_with_a_hint()
    {
        var error = Assert.Throws<FarolException>(() => WorkspaceDiscovery.Resolve("missing/App.sln", _temp));

        Assert.Equal(ErrorCodes.WorkspaceNotFound, error.Code);
        Assert.EndsWith("a relative path starts at the root, as responses write paths.", error.Hint, StringComparison.Ordinal);
    }

    /// <summary>AC-30: the candidates in a folder below the root are written from the root, so they can be passed back as they are.</summary>
    [Fact]
    public void Paths_in_discovery_errors_are_relative_to_the_root()
    {
        var repo = Directory.CreateDirectory(Path.Combine(_temp, "repo")).FullName;
        File.WriteAllText(Path.Combine(repo, "Alpha.sln"), string.Empty);
        File.WriteAllText(Path.Combine(repo, "Beta.sln"), string.Empty);
        var empty = Directory.CreateDirectory(Path.Combine(_temp, "empty")).FullName;

        var ambiguous = Assert.Throws<FarolException>(() => WorkspaceDiscovery.Resolve("repo", _temp));
        var picked = WorkspaceDiscovery.Resolve("repo/Alpha.sln", _temp);
        var noneBelow = Assert.Throws<FarolException>(() => WorkspaceDiscovery.Resolve("empty", _temp));
        var noneInRoot = Assert.Throws<FarolException>(() => WorkspaceDiscovery.Resolve(null, empty));

        Assert.Equal("Found 2 candidates: repo/Alpha.sln, repo/Beta.sln.", ambiguous.Message);
        Assert.Equal(Path.Combine(repo, "Alpha.sln"), picked.Path, ignoreCase: true);
        Assert.Equal("No .sln, .slnx, .csproj or .vbproj found in 'empty'.", noneBelow.Message);
        Assert.Equal("No .sln, .slnx, .csproj or .vbproj found in the root.", noneInRoot.Message);
    }

    private static Undo DenyListing(string directory) =>
        OperatingSystem.IsWindows() ? DenyListingOnWindows(directory) : DenyListingOnUnix(directory);

    [UnsupportedOSPlatform("windows")]
    private static Undo DenyListingOnUnix(string directory)
    {
        File.SetUnixFileMode(directory, UnixFileMode.None);
        return new Undo(() => File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute));
    }

    [SupportedOSPlatform("windows")]
    private static Undo DenyListingOnWindows(string directory)
    {
        var folder = new DirectoryInfo(directory);
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var security = folder.GetAccessControl();
        security.AddAccessRule(rule);
        folder.SetAccessControl(security);
        return new Undo(() =>
        {
            var restored = folder.GetAccessControl();
            restored.RemoveAccessRule(rule);
            folder.SetAccessControl(restored);
        });
    }

    private sealed class Undo(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
