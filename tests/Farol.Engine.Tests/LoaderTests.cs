using System.Diagnostics;
using Farol.Engine.Loading;
using Farol.Engine.Workspaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Farol.Engine.Tests;

public sealed class LoaderTests
{
    /// <summary>
    /// A source generator project that was never built leaves an unresolved analyzer reference behind (DNN Platform has
    /// one in about thirty projects). Roslyn's checksum service rejects such references, which made every reference and
    /// implementation search through those projects fail; the loader drops them and says what to build.
    /// </summary>
    [Fact]
    public async Task An_unresolved_analyzer_is_dropped_with_a_hint_and_searches_work()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = new AdhocWorkspace();
        var missing = Path.Combine(Path.GetTempPath(), "farol-not-built", "Shop.Generators.dll");
        var project = workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(), VersionStamp.Default, "Shop", "Shop", LanguageNames.CSharp, analyzerReferences: [new UnresolvedAnalyzerReference(missing)]));
        var document = workspace.AddDocument(project.Id, "Cart.cs", SourceText.From("class Cart { void Add() { } void AddTwice() { Add(); Add(); } }"));

        var (solution, issues) = MSBuildWorkspaceLoader.WithoutUnresolvedAnalyzers(document.Project.Solution);
        var cart = solution.GetDocument(document.Id)!;
        var root = await cart.GetSyntaxRootAsync(ct);
        var model = await cart.GetSemanticModelAsync(ct);
        var add = model!.GetDeclaredSymbol(root!.DescendantNodes().OfType<MethodDeclarationSyntax>().First(), ct)!;
        var references = await SymbolFinder.FindReferencesAsync(add, solution, ct);

        Assert.Empty(solution.GetProject(project.Id)!.AnalyzerReferences);
        Assert.Equal(2, references.SelectMany(r => r.Locations).Count());
        var issue = Assert.Single(issues);
        Assert.Equal("warning", issue.Severity);
        Assert.Contains(missing, issue.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// AC-47: a folder of analyzers that cannot be copied, such as one a build is writing, loads from its own files once
    /// the build is done, with a load issue; the other folders still load from copies.
    /// </summary>
    [Fact]
    public void Analyzers_that_cannot_be_copied_load_from_their_own_files_with_a_load_issue()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to read a file another program holds.");
        var temp = Directory.CreateTempSubdirectory("farol-analyzers-").FullName;
        try
        {
            var busy = Path.Combine(temp, "busy", "Busy.Generator.dll");
            var free = Path.Combine(temp, "free", "Free.Generator.dll");
            Emit(busy, "public static class Busy { }");
            Emit(free, "public static class Free { }");

            (Solution Solution, List<LoadIssue> Issues) result;
            using (new FileStream(busy, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                result = AnalyzerCopies.WithCopies(Solution(busy, free));
            }

            var references = References(result.Solution);
            Assert.Equal(busy, references[0].FullPath);
            Assert.Equal("Busy.Generator", references[0].GetAssembly().GetName().Name);
            Assert.NotEqual(free, references[1].FullPath);
            Assert.Equal(references[1].FullPath, references[1].GetAssembly().Location, ignoreCase: true);
            var issue = Assert.Single(result.Issues);
            Assert.Equal("warning", issue.Severity);
            Assert.StartsWith($"Farol could not copy the analyzers in {Path.GetDirectoryName(busy)} (", issue.Message, StringComparison.Ordinal);
            Assert.EndsWith("so rebuilding them fails while it runs.", issue.Message, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (UnauthorizedAccessException)
            {
                // The analyzer loaded from its own file stays locked until the test process exits.
            }
        }
    }

    /// <summary>
    /// AC-47: a reload after a rebuild loads again the analyzers that changed and those that use them, with the new code;
    /// a folder that needs nothing from the others, such as an SDK's, is loaded once.
    /// </summary>
    [Fact]
    public void A_reload_loads_again_only_the_analyzers_that_changed_or_use_what_changed()
    {
        var temp = Directory.CreateTempSubdirectory("farol-analyzers-").FullName;
        try
        {
            var sdk = Path.Combine(temp, "sdk", "Sdk.Analyzers.dll");
            var words = Path.Combine(temp, "words", "Shop.Words.dll");
            var generator = Path.Combine(temp, "generator", "Shop.Generator.dll");
            Emit(sdk, "public static class SdkAnalyzers { }");
            Emit(words, "public static class Words { public static string Hello() => \"Hello\"; }");
            Emit(generator, "public static class Generator { public static string Text() => Words.Hello(); }", words);
            var solution = Solution(sdk, words, generator);

            var first = References(AnalyzerCopies.WithCopies(solution).Solution);
            var before = Text(first[2]);
            Emit(words, "public static class Words { public static string Hello() => \"Hi\"; }");
            var second = References(AnalyzerCopies.WithCopies(solution).Solution);

            Assert.Same(first[0].GetAssembly(), second[0].GetAssembly());
            Assert.NotSame(first[2].GetAssembly(), second[2].GetAssembly());
            Assert.Equal("Hello", before);
            Assert.Equal("Hi", Text(second[2]));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    /// <summary>AC-47: the analyzer copies of a process that has exited are removed; those of a running one are kept.</summary>
    [Fact]
    public void Analyzer_copies_are_removed_once_their_process_has_exited()
    {
        var root = Directory.CreateTempSubdirectory("farol-analyzers-").FullName;
        try
        {
            using var exited = Process.Start(new ProcessStartInfo("dotnet", "--version") { RedirectStandardOutput = true })!;
            exited.WaitForExit();
            var gone = Directory.CreateDirectory(Path.Combine(root, $"{exited.Id}-0")).FullName;
            File.WriteAllText(Path.Combine(gone, "Old.Generator.dll"), string.Empty);
            var running = Directory.CreateDirectory(Path.Combine(root, $"{Environment.ProcessId}-0")).FullName;
            var other = Directory.CreateDirectory(Path.Combine(root, "not-a-server")).FullName;

            AnalyzerCopies.RemoveLeftovers(root);

            Assert.False(Directory.Exists(gone));
            Assert.True(Directory.Exists(running));
            Assert.True(Directory.Exists(other));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
    private static Solution Solution(params string[] analyzers)
    {
        var workspace = new AdhocWorkspace();
        var loader = new AnalyzerCompatibilityTests.NoLoader();
        return workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(), VersionStamp.Default, "Shop", "Shop", LanguageNames.CSharp,
            analyzerReferences: [.. analyzers.Select(a => new AnalyzerFileReference(a, loader))])).Solution;
    }

    private static AnalyzerFileReference[] References(Solution solution) =>
        [.. solution.Projects.Single().AnalyzerReferences.OfType<AnalyzerFileReference>()];

    private static string? Text(AnalyzerFileReference generator) =>
        (string?)generator.GetAssembly().GetType("Generator")!.GetMethod("Text")!.Invoke(null, null);

    private static void Emit(string path, string code, params string[] references)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var result = CSharpCompilation.Create(
                Path.GetFileNameWithoutExtension(path),
                [CSharpSyntaxTree.ParseText(code)],
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location), .. references.Select(r => MetadataReference.CreateFromFile(r))],
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .Emit(path);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
    }
}
