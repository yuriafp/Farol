using Farol.Engine.Building;
using Farol.Engine.Testing;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Farol.Engine.Tests;

/// <summary>Command lines, filters and result parsing for every test host, without running anything.</summary>
public sealed class TestCommandsTests
{
    private static readonly ProjectId Variant = ProjectId.CreateNewId();

    [Fact]
    public void Exact_test_names_become_an_escaped_vstest_filter()
    {
        var target = new TestTarget(Project(TestFrameworks.XUnit), ["App.Tests.PriceTests.Total", "App.Tests.Outer+Inner.Odd(Name)"]);

        Assert.Equal(
            @"FullyQualifiedName=App.Tests.PriceTests.Total|FullyQualifiedName=App.Tests.Outer+Inner.Odd\(Name\)",
            TestCommands.Filter(target));
    }

    [Fact]
    public void NUnit_filters_also_match_parameterized_cases()
    {
        var target = new TestTarget(Project(TestFrameworks.NUnit), ["App.Tests.PriceTests.Total"]);

        Assert.Equal(@"FullyQualifiedName=App.Tests.PriceTests.Total|FullyQualifiedName~App.Tests.PriceTests.Total\(", TestCommands.Filter(target));
    }

    [Fact]
    public void A_filter_too_long_for_a_command_line_falls_back_to_classes()
    {
        var names = Enumerable.Range(0, 400).Select(i => $"App.Tests.VeryLongTestClassNameForPricing.Test_number_{i}_with_a_descriptive_name").ToList();

        Assert.Equal("FullyQualifiedName~App.Tests.VeryLongTestClassNameForPricing.", TestCommands.Filter(new TestTarget(Project(TestFrameworks.XUnit), names)));
    }

    [Fact]
    public void Each_host_gets_its_own_trx_options()
    {
        var all = new TestTarget(Project(TestFrameworks.MSTest), NameContains: "Pricing");
        var xunit = new TestTarget(Project(TestFrameworks.XUnit), NameContains: "Pricing");

        var vstest = TestCommands.Arguments(TestHost.VSTestConsole, all, @"C:\repo\bin\App.Tests.dll", "Debug", @"C:\results", "App.Tests.trx");
        var dotnet = TestCommands.Arguments(TestHost.DotnetTestVSTest, all, "App.Tests.csproj", "Release", @"C:\results", "App.Tests.trx");
        var platform = TestCommands.Arguments(TestHost.DotnetTestPlatform, xunit, "App.Tests.csproj", "Debug", @"C:\results", "App.Tests.trx");
        var platformMSTest = TestCommands.Arguments(TestHost.DotnetTestPlatform, all, "App.Tests.csproj", "Debug", @"C:\results", "App.Tests.trx");

        Assert.Equal([@"C:\repo\bin\App.Tests.dll", "/Logger:trx;LogFileName=App.Tests.trx", @"/ResultsDirectory:C:\results", "/TestCaseFilter:FullyQualifiedName~Pricing"], vstest);
        Assert.Equal(["test", "App.Tests.csproj", "--no-build", "-c", "Release", "--logger", "trx;LogFileName=App.Tests.trx", "--results-directory", @"C:\results", "--filter", "FullyQualifiedName~Pricing"], dotnet);
        Assert.Contains("--report-xunit-trx", platform);
        Assert.Equal(["--filter", "FullyQualifiedName~Pricing"], platform.TakeLast(2));
        Assert.Contains("--report-trx", platformMSTest);
    }

    [Fact]
    public void TUnit_runs_unfiltered_on_the_testing_platform()
    {
        var target = new TestTarget(Project(TestFrameworks.TUnit), ["App.Tests.PriceTests.Total"]);

        Assert.DoesNotContain("--filter", TestCommands.Arguments(TestHost.DotnetTestPlatform, target, "App.Tests.csproj", "Debug", "results", "App.Tests.trx"));
    }

    [Fact]
    public void Global_json_decides_between_vstest_and_the_testing_platform()
    {
        var root = Directory.CreateTempSubdirectory("farol-globaljson-").FullName;
        try
        {
            var project = Directory.CreateDirectory(Path.Combine(root, "tests", "App.Tests")).FullName;
            var vstest = TestCommands.UsesTestingPlatform(project);
            File.WriteAllText(Path.Combine(root, "global.json"), """{ "sdk": { "version": "10.0.100" }, "test": { "runner": "Microsoft.Testing.Platform" } }""");

            Assert.False(vstest);
            Assert.True(TestCommands.UsesTestingPlatform(project));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Trx_results_keep_outcomes_messages_and_stack_traces()
    {
        var path = Path.Combine(Path.GetTempPath(), $"farol-{Guid.NewGuid():N}.trx");
        File.WriteAllText(path, """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testName="App.Tests.PriceTests.Total" outcome="Failed">
                  <Output><ErrorInfo><Message>Assert.Equal() Failure</Message><StackTrace>   at App.Tests.PriceTests.Total() in C:\repo\tests\PriceTests.cs:line 16</StackTrace></ErrorInfo></Output>
                </UnitTestResult>
                <UnitTestResult testName="App.Tests.PriceTests.Rounds(price: 1)" outcome="Passed" />
                <UnitTestResult testName="App.Tests.PriceTests.Later" outcome="NotExecuted" />
              </Results>
            </TestRun>
            """);
        try
        {
            var results = TrxReader.Read(path)!;

            Assert.Equal(["Failed", "Passed", "NotExecuted"], results.Select(r => r.Outcome));
            Assert.True(results[0].Failed);
            Assert.Equal("Assert.Equal() Failure", results[0].Message);
            Assert.Contains("PriceTests.cs:line 16", results[0].StackTrace, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Only_frames_in_user_code_are_kept_in_any_language()
    {
        const string root = @"C:\repo";
        const string trace = """
               at App.Core.Pricing.Total(Decimal price) in C:\repo\src\Pricing.cs:line 19
               at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)
               em App.Tests.PriceTests.Total() na C:\repo\tests\PriceTests.cs:linha 16
               at Xunit.Sdk.Runner.Run() in C:\agent\xunit\src\Runner.cs:line 300
            """;

        var frames = StackFrames.InUserCode(trace, root, path => path.StartsWith(root, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(
            ["at App.Core.Pricing.Total(Decimal price) · src/Pricing.cs:19", "at App.Tests.PriceTests.Total() · tests/PriceTests.cs:16"],
            frames);
    }

    [Fact]
    public void Console_output_is_the_fallback_when_the_binary_log_cannot_be_read()
    {
        const string output = """
            C:\repo\src\Core\Price.cs(19,20): error CS0019: Operator '*' cannot be applied [C:\repo\src\Core\Core.csproj::TargetFramework=net48]
            C:\repo\src\Core\Price.cs(19,20): error CS0019: Operator '*' cannot be applied [C:\repo\src\Core\Core.csproj::TargetFramework=net10.0]
            C:\repo\src\Core\Old.cs(3,7): warning CS0618: 'Old' is obsolete [C:\repo\src\Core\Core.csproj]
            """;

        var (errors, warnings) = BuildLogReader.ReadConsoleOutput(output).Issues();

        var error = Assert.Single(errors);
        Assert.Equal(("CS0019", 19, 20, "Core"), (error.Code, error.Line, error.Column, error.Project));
        Assert.Equal(["net10.0", "net48"], error.TargetFrameworks);
        Assert.Equal("CS0618", Assert.Single(warnings).Code);
    }

    private static TestProjectInfo Project(string framework) =>
        new("App.Tests", @"C:\repo\tests\App.Tests\App.Tests.csproj", IsSdkStyle: true, [framework], [Variant]);
}
