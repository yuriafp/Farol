using Farol.Core.Execution;
using Farol.Testing;
using Xunit;

namespace Farol.Tools.Tests;

public sealed class AnalyzerLoadingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// AC-47: the solution references its generator and the generator's library, each from its own bin folder. With
    /// Farol holding the workspace, both rebuild, and a reload runs the new code rather than an earlier copy of it.
    /// </summary>
    [Fact]
    public async Task AC47_the_solution_builds_its_own_generator_while_Farol_holds_it()
    {
        using var copy = FixtureCopy.Create(TestPaths.GeneratorsDirectory);
        var build = await DotnetAsync(copy.Root, "build", copy.PathOf("Generators.slnx"));
        Assert.True(build.ExitCode == 0, build.StandardOutput);
        await using var harness = await McpHarness.StartAsync(copy.Root, Ct);

        var check = await harness.CallAsync("dotnet_check", new() { ["scope"] = "solution", ["includeExisting"] = true }, Ct);
        var before = await harness.CallAsync("dotnet_symbol", new() { ["symbol"] = "Greeting.Generated.Text", ["include"] = "source" }, Ct);
        var words = copy.PathOf("src", "Greeting.Words", "Words.cs");
        await File.WriteAllTextAsync(words, (await File.ReadAllTextAsync(words, Ct)).Replace("\"Hello\"", "\"Hi\"", StringComparison.Ordinal), Ct);
        var rebuild = await DotnetAsync(copy.Root, "build", copy.PathOf("src", "Greeting.Generator", "Greeting.Generator.csproj"), "--no-restore");
        var reload = await harness.CallAsync("dotnet_workspace", new() { ["action"] = "reload" }, Ct);
        var after = await harness.CallAsync("dotnet_symbol", new() { ["symbol"] = "Greeting.Generated.Text", ["include"] = "source" }, Ct);

        Assert.False(check.IsError, check.Text);
        Assert.Contains("checked projects: Greeting.App, Greeting.Generator, Greeting.Words", check.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("error CS", check.Text, StringComparison.Ordinal);
        Assert.False(before.IsError, before.Text);
        Assert.Contains("\"Hello\"", before.Text, StringComparison.Ordinal);
        Assert.True(rebuild.ExitCode == 0, rebuild.StandardOutput);
        Assert.False(reload.IsError, reload.Text);
        Assert.Contains("\"Hi\"", after.Text, StringComparison.Ordinal);
    }

    private static Task<ProcessResult> DotnetAsync(string directory, params string[] arguments) =>
        new LocalProcessRunner().RunAsync(
            new ProcessSpec("dotnet", [.. arguments, "-nodeReuse:false", "-v:q", "-nologo"], directory, TimeSpan.FromMinutes(5)),
            Ct);
}
