using System.Globalization;
using System.Text;
using Farol.Engine.CodeActions;
using Farol.Testing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Farol.Engine.Tests;

/// <summary>
/// Slice 0 of the Phase 2 plan: which of Roslyn's refactorings run without an editor on the legacy fixture. Writes
/// artifacts/spikes/phase-2-refactorings.md.
/// </summary>
[Collection(LegacyFixtureDefinition.Name)]
public sealed class Phase2RefactoringSpikeTests(LegacyWorkspaceFixture fixture)
{
    private const string Calculator = "OrderCalculator.cs";

    [Fact]
    public async Task Spike_refactorings_without_an_editor()
    {
        Assert.SkipUnless(fixture.Available, "Needs Windows with Visual Studio or Build Tools.");
        var ct = TestContext.Current.CancellationToken;
        var solution = fixture.Snapshot!.Solution;
        var document = solution.Projects.SelectMany(p => p.Documents).Single(d => d.Name == Calculator);
        var text = await document.GetTextAsync(ct);
        var report = new StringBuilder("# phase-2-refactorings\n\n");
        var invariant = CultureInfo.InvariantCulture;

        // 1. Rename through the public Renamer.
        var model = await document.GetSemanticModelAsync(ct);
        var root = await document.GetSyntaxRootAsync(ct);
        var getTotalSpan = text.ToString().IndexOf("GetTotal(int", StringComparison.Ordinal);
        var symbol = model!.GetDeclaredSymbol(root!.FindToken(getTotalSpan).Parent!, ct)!;
        var renamed = await Renamer.RenameSymbolAsync(solution, symbol, new SymbolRenameOptions(), "CalculateTotal", ct);
        var renamedDocs = renamed.GetChanges(solution).GetProjectChanges().SelectMany(p => p.GetChangedDocuments()).Select(id => renamed.GetDocument(id)!).ToList();
        report.AppendLine(invariant, $"rename GetTotal → CalculateTotal: {renamedDocs.Count} documents changed: {string.Join(", ", renamedDocs.Select(d => $"{d.Name} ({d.Project.Language})"))}");
        var refs = await SymbolFinder.FindReferencesAsync(symbol, solution, ct);
        report.AppendLine(invariant, $"  references before: {refs.SelectMany(r => r.Locations).Count()}");

        // 2–4. The refactoring providers at chosen spans, and the shape of their actions.
        var statements = Span(text, "var order = _repository.Find(orderId);", "return order.Subtotal() * (1 + TaxRate());");
        var signature = new TextSpan(getTotalSpan, 0);
        var typeName = new TextSpan(text.ToString().IndexOf("OrderCalculator", text.ToString().IndexOf("class", StringComparison.Ordinal), StringComparison.Ordinal), 0);
        foreach (var (label, span) in new[] { ("statements of GetTotal", statements), ("GetTotal's name", signature), ("class name", typeName) })
        {
            report.AppendLine();
            report.AppendLine(invariant, $"## actions at {label}");
            foreach (var provider in CodeActionProviders.RefactoringsFor(LanguageNames.CSharp))
            {
                var actions = new List<CodeAction>();
                try
                {
                    await provider.ComputeRefactoringsAsync(new CodeRefactoringContext(document, span, actions.Add, ct));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    report.AppendLine(invariant, $"- {provider.GetType().Name}: threw {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                foreach (var action in actions.SelectMany(Leaves))
                {
                    var kind = action is CodeActionWithOptions ? "WITH OPTIONS" : "plain";
                    var applied = "";
                    if (action is not CodeActionWithOptions && (action.Title.Contains("xtract", StringComparison.Ordinal) || action.Title.Contains("ove", StringComparison.Ordinal)))
                    {
                        try
                        {
                            var operations = await action.GetOperationsAsync(solution, new NoProgress(), ct);
                            var changed = operations.OfType<ApplyChangesOperation>().SingleOrDefault()?.ChangedSolution;
                            var docs = changed?.GetChanges(solution).GetProjectChanges().SelectMany(p => p.GetChangedDocuments().Concat(p.GetAddedDocuments())).Count() ?? 0;
                            applied = $" → {operations.Length} operation(s), {docs} document(s) changed/added";
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            applied = $" → apply threw {ex.GetType().Name}: {ex.Message}";
                        }
                    }

                    report.AppendLine(invariant, $"- {provider.GetType().Name}: \"{action.Title}\" ({kind}){applied}");
                }
            }
        }

        Directory.CreateDirectory(TestPaths.SpikeReports);
        await File.WriteAllTextAsync(Path.Combine(TestPaths.SpikeReports, "phase-2-refactorings.md"), report.ToString(), ct);
        Assert.NotEmpty(renamedDocs);
    }

    private static TextSpan Span(SourceText text, string first, string last)
    {
        var s = text.ToString();
        var start = s.IndexOf(first, StringComparison.Ordinal);
        var end = s.IndexOf(last, StringComparison.Ordinal) + last.Length;
        return TextSpan.FromBounds(start, end);
    }

    private static IEnumerable<CodeAction> Leaves(CodeAction action) =>
        action.NestedActions.IsDefaultOrEmpty ? [action] : action.NestedActions.SelectMany(Leaves);

    private sealed class NoProgress : IProgress<CodeAnalysisProgress>
    {
        public void Report(CodeAnalysisProgress value)
        {
        }
    }
}
