using Farol.Core.Execution;
using Farol.Testing;
using Xunit;

namespace Farol.Tools.Tests;

public sealed class AnalyzerLoadingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AC47_the_solution_builds_its_own_generator_while_Farol_holds_it()
    {
        using var copy = FixtureCopy.Create(TestPaths.GeneratorsDirectory);
        var build = await DotnetAsync(copy.Root, "build", copy.PathOf("Generators.slnx"));
        Assert.True(build.ExitCode == 0, build.StandardOutput);
        await using var harness = await McpHarness.StartAsync(copy.Root, Ct);

        var check = await harness.CallAsync("dotnet_check", new() { ["scope"] = "solution", ["includeExisting"] = true }, Ct);
        var generator = copy.PathOf("src", "Greeting.Generator", "GreetingGenerator.cs");
        await File.WriteAllTextAsync(generator, (await File.ReadAllTextAsync(generator, Ct)).Replace("\\\"Hello\\\"", "\\\"Hi\\\"", StringComparison.Ordinal), Ct);
        var rebuild = await DotnetAsync(copy.Root, "build", copy.PathOf("src", "Greeting.Generator", "Greeting.Generator.csproj"), "--no-restore");

        Assert.False(check.IsError, check.Text);
        Assert.Contains("checked projects: Greeting.App, Greeting.Generator", check.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("error CS", check.Text, StringComparison.Ordinal);
        Assert.True(rebuild.ExitCode == 0, rebuild.StandardOutput);
    }

    private static Task<ProcessResult> DotnetAsync(string directory, params string[] arguments) =>
        new LocalProcessRunner().RunAsync(
            new ProcessSpec("dotnet", [.. arguments, "-nodeReuse:false", "-v:q", "-nologo"], directory, TimeSpan.FromMinutes(5)),
            Ct);
}
