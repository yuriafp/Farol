using Farol.Core;
using Farol.Core.Execution;
using Farol.Core.Paths;
using Xunit;

namespace Farol.Engine.Tests;

/// <summary>Every path a tool reads or writes must stay inside the root or a trusted path, whatever '..' or links say.</summary>
public sealed class PathSandboxTests : IDisposable
{
    private readonly string _base = Directory.CreateTempSubdirectory("farol-sandbox-").FullName;

    private string Root => Path.Combine(_base, "repo");

    private string Outside => Path.Combine(_base, "elsewhere");

    public PathSandboxTests()
    {
        Directory.CreateDirectory(Path.Combine(Root, "src"));
        Directory.CreateDirectory(Outside);
        File.WriteAllText(Path.Combine(Outside, "secret.cs"), "class Secret {}");
    }

    [Fact]
    public void Relative_paths_resolve_against_the_root()
    {
        var sandbox = new PathSandbox(Root);

        Assert.Equal(Path.Combine(Root, "src", "A.cs"), sandbox.Resolve("src/A.cs"));
    }

    [Fact]
    public void AC42_a_relative_path_missing_under_the_root_resolves_from_the_workspace_folder()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(Root, "App", "src")).Parent!.FullName;
        File.WriteAllText(Path.Combine(workspace, "src", "B.cs"), "class B {}");
        File.WriteAllText(Path.Combine(Root, "src", "A.cs"), "class A {}");
        File.WriteAllText(Path.Combine(workspace, "src", "A.cs"), "class A {}");
        var sandbox = new PathSandbox(Root);

        Assert.Equal(Path.Combine(workspace, "src", "B.cs"), sandbox.Resolve("src/B.cs", workspace));
        Assert.Equal(Path.Combine(Root, "src", "A.cs"), sandbox.Resolve("src/A.cs", workspace));
        Assert.Equal(Path.Combine(Root, "src", "C.cs"), sandbox.Resolve("src/C.cs", workspace));
    }

    [Fact]
    public void The_workspace_folder_cannot_lead_outside_the_root()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(Root, "App")).FullName;
        var sandbox = new PathSandbox(Root);

        Assert.True(File.Exists(Path.Combine(workspace, "..", "..", "elsewhere", "secret.cs")));
        var error = Assert.Throws<FarolException>(() => sandbox.Resolve("../../elsewhere/secret.cs", workspace));
        Assert.Equal(ErrorCodes.PathNotTrusted, error.Code);
    }

    [Fact]
    public void AC42_a_workspace_under_a_trusted_path_resolves_paths_from_its_folder_only_while_trusted()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(Outside, "App", "src")).Parent!.FullName;
        File.WriteAllText(Path.Combine(workspace, "src", "B.cs"), "class B {}");

        Assert.Equal(Path.Combine(workspace, "src", "B.cs"), new PathSandbox(Root, [Outside]).Resolve("src/B.cs", workspace));
        Assert.Equal(Path.Combine(Root, "src", "B.cs"), new PathSandbox(Root).Resolve("src/B.cs", workspace));
    }

    [Theory]
    [InlineData("../elsewhere/secret.cs")]
    [InlineData("src/../../elsewhere/secret.cs")]
    public void Dot_dot_cannot_leave_the_root(string path)
    {
        var sandbox = new PathSandbox(Root);

        var error = Assert.Throws<FarolException>(() => sandbox.Resolve(path));

        Assert.Equal(ErrorCodes.PathNotTrusted, error.Code);
        Assert.Contains("Farol:TrustedPaths", error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sibling_sharing_the_root_prefix_is_outside()
    {
        Directory.CreateDirectory(Root + "2");
        var sandbox = new PathSandbox(Root);

        Assert.False(sandbox.Contains(Path.Combine(Root + "2", "A.cs")));
    }

    [Fact]
    public void Trusted_paths_extend_the_sandbox()
    {
        var sandbox = new PathSandbox(Root, [Outside]);

        Assert.Equal(Path.Combine(Outside, "secret.cs"), sandbox.Resolve(Path.Combine(Outside, "secret.cs")));
    }

    [Fact]
    public async Task A_link_inside_the_root_cannot_lead_outside()
    {
        var link = Path.Combine(Root, "src", "linked");
        Assert.SkipUnless(await TryCreateDirectoryLinkAsync(link, Outside), "Creating a junction or symbolic link is not possible here.");
        var sandbox = new PathSandbox(Root);

        Assert.True(File.Exists(Path.Combine(link, "secret.cs")));
        Assert.Throws<FarolException>(() => sandbox.Resolve("src/linked/secret.cs"));
        Assert.True(new PathSandbox(Root, [Outside]).Contains(Path.Combine(link, "secret.cs")));
    }

    public void Dispose()
    {
        try
        {
            // Remove the link itself first: a recursive delete must not walk into its target.
            var link = Path.Combine(Root, "src", "linked");
            if (new DirectoryInfo(link).LinkTarget is not null)
            {
                Directory.Delete(link);
            }

            Directory.Delete(_base, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    // Junctions need no privilege on Windows; symbolic links do (or developer mode).
    private static async Task<bool> TryCreateDirectoryLinkAsync(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            var result = await new LocalProcessRunner().RunAsync(
                new ProcessSpec("cmd", ["/c", "mklink", "/J", link, target], Path.GetDirectoryName(link)!, TimeSpan.FromSeconds(30)),
                TestContext.Current.CancellationToken);
            if (result.ExitCode == 0)
            {
                return true;
            }
        }

        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
