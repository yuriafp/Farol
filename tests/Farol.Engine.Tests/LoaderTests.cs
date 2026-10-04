using Farol.Engine.Loading;
using Microsoft.CodeAnalysis;
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
}
