using System.Reflection;
using Farol.Engine.Loading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Farol.Engine.Tests;

/// <summary>
/// AC-46, on a fake .NET installation: SDK 11 preview's generator needs a compiler newer than Farol's, SDK 10.0.401 and
/// 10.0.102 have the same generator for an older one, and SDK 9 lacks one of the generator's files.
/// </summary>
public sealed class AnalyzerCompatibilityTests : IDisposable
{
    private const string Generators = "Sdks/Microsoft.NET.Sdk.Razor/source-generators";

    private static readonly Version Newer = new(99, 0, 0, 0);
    private static readonly Version Older = new(1, 0, 0, 0);

    private readonly string _temp = Directory.CreateTempSubdirectory("farol-analyzers-").FullName;

    public AnalyzerCompatibilityTests()
    {
        Directory.CreateDirectory(Path.Combine(_temp, "dotnet"));
        File.WriteAllText(Path.Combine(_temp, "dotnet", "dotnet.exe"), string.Empty);
        Emit(Sdk("11.0.100-rc.2.26504.105", "Razor.Compiler.dll"), Newer);
        Emit(Sdk("11.0.100-rc.2.26504.105", "Razor.Utilities.dll"), compiler: null);
        Emit(Sdk("10.0.401", "Razor.Compiler.dll"), Older);
        Emit(Sdk("10.0.401", "Razor.Utilities.dll"), compiler: null);
        Emit(Sdk("10.0.102", "Razor.Compiler.dll"), Older);
        Emit(Sdk("10.0.102", "Razor.Utilities.dll"), compiler: null);
        Emit(Sdk("9.0.318", "Razor.Compiler.dll"), Older);
        Emit(Package("Shop.Generators.dll"), Newer);
        Emit(Package("Shop.Analyzers.dll"), Older);
        Directory.CreateDirectory(Path.Combine(_temp, "repo"));
    }

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    [Fact]
    public void An_SDK_analyzer_for_a_newer_compiler_comes_from_the_newest_SDK_whose_compiler_Farol_runs()
    {
        var solution = Solution(Sdk("11.0.100-rc.2.26504.105", "Razor.Compiler.dll"), Sdk("11.0.100-rc.2.26504.105", "Razor.Utilities.dll"), Package("Shop.Analyzers.dll"));

        var (result, issues) = AnalyzerCompatibility.WithRunnableAnalyzers(solution, Path.Combine(_temp, "repo"));

        Assert.Equal(
            [Sdk("10.0.401", "Razor.Compiler.dll"), Sdk("10.0.401", "Razor.Utilities.dll"), Package("Shop.Analyzers.dll")],
            Paths(result));
        var issue = Assert.Single(issues);
        Assert.Equal("warning", issue.Severity);
        Assert.StartsWith("Razor.Compiler.dll of .NET SDK 11.0.100-rc.2.26504.105 needs Microsoft.CodeAnalysis 99.0.0.0, newer than the ", issue.Message, StringComparison.Ordinal);
        Assert.Contains("Farol takes it from .NET SDK 10.0.401 instead, in 1 project(s): Blazor.", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pinned_SDK_or_a_package_analyzer_for_a_newer_compiler_is_an_error_that_says_how_to_fix_it()
    {
        File.WriteAllText(Path.Combine(_temp, "global.json"), "{ // the SDK the team uses\n  \"sdk\": { \"version\": \"11.0.100-rc.2.26504.105\" },\n}");
        var solution = Solution(Sdk("11.0.100-rc.2.26504.105", "Razor.Compiler.dll"), Package("Shop.Generators.dll"));

        var (result, issues) = AnalyzerCompatibility.WithRunnableAnalyzers(solution, Path.Combine(_temp, "repo"));

        Assert.Equal([Sdk("11.0.100-rc.2.26504.105", "Razor.Compiler.dll"), Package("Shop.Generators.dll")], Paths(result));
        Assert.Equal(2, issues.Count);
        Assert.All(issues, i => Assert.Equal("error", i.Severity));
        Assert.Contains(
            $"Analyzer or source generator {Sdk("11.0.100-rc.2.26504.105", "Razor.Compiler.dll")} needs Microsoft.CodeAnalysis 99.0.0.0",
            issues[0].Message,
            StringComparison.Ordinal);
        Assert.Contains("so the code it generates is missing from 1 project(s): Blazor", issues[0].Message, StringComparison.Ordinal);
        Assert.EndsWith($"{Path.Combine(_temp, "global.json")} pins .NET SDK 11.0.100-rc.2.26504.105; until a Farol version runs compiler 99.0.0.0, pin SDK 10.0.401, which is installed, instead.", issues[0].Message, StringComparison.Ordinal);
        Assert.EndsWith("Use a version of it built for Microsoft.CodeAnalysis " + typeof(Compilation).Assembly.GetName().Version + " or earlier, or a Farol version that runs compiler 99.0.0.0.", issues[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyzers_for_the_compiler_Farol_runs_are_left_as_they_are()
    {
        var solution = Solution(Sdk("10.0.102", "Razor.Compiler.dll"), Package("Shop.Analyzers.dll"));

        var (result, issues) = AnalyzerCompatibility.WithRunnableAnalyzers(solution, Path.Combine(_temp, "repo"));

        Assert.Same(solution, result);
        Assert.Empty(issues);
        Assert.Equal(Older, AnalyzerCompatibility.CompilerVersion(Package("Shop.Analyzers.dll")));
        Assert.Null(AnalyzerCompatibility.CompilerVersion(Sdk("10.0.401", "Razor.Utilities.dll")));
        Assert.Null(AnalyzerCompatibility.CompilerVersion(Path.Combine(_temp, "dotnet", "dotnet.exe")));
    }

    private string Sdk(string version, string file) => Path.GetFullPath(Path.Combine(_temp, "dotnet", "sdk", version, Generators, file));

    private string Package(string file) => Path.Combine(_temp, "packages", "shop", "1.0.0", "analyzers", "dotnet", "cs", file);

    private static Solution Solution(params string[] analyzers)
    {
        var workspace = new AdhocWorkspace();
        var loader = new NoLoader();
        return workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(), VersionStamp.Default, "Blazor", "Blazor", LanguageNames.CSharp,
            analyzerReferences: [.. analyzers.Select(a => new AnalyzerFileReference(a, loader))])).Solution;
    }

    private static string[] Paths(Solution solution) =>
        [.. solution.Projects.Single().AnalyzerReferences.OfType<AnalyzerFileReference>().Select(r => r.FullPath)];

    /// <summary>An assembly that references Microsoft.CodeAnalysis <paramref name="compiler"/>, or nothing of it.</summary>
    private static void Emit(string path, Version? compiler)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        MetadataReference[] references = [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)];
        var code = "public static class Marker { }";
        if (compiler is not null)
        {
            var fake = CSharpCompilation.Create(
                "Microsoft.CodeAnalysis",
                [CSharpSyntaxTree.ParseText($"[assembly: System.Reflection.AssemblyVersion(\"{compiler}\")] namespace Microsoft.CodeAnalysis {{ public sealed class Compiler {{ }} }}")],
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            references = [.. references, fake.ToMetadataReference()];
            code = "public static class Marker { public static readonly System.Type Uses = typeof(Microsoft.CodeAnalysis.Compiler); }";
        }

        var result = CSharpCompilation.Create(Path.GetFileNameWithoutExtension(path), [CSharpSyntaxTree.ParseText(code)], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .Emit(path);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
    }

    private sealed class NoLoader : IAnalyzerAssemblyLoader
    {
        public void AddDependencyLocation(string fullPath)
        {
        }

        public Assembly LoadFromPath(string fullPath) => throw new InvalidOperationException("The test never runs an analyzer.");
    }
}
