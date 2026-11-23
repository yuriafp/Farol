using System.Collections.Immutable;
using Farol.Engine.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Farol.Engine.Tests;

/// <summary>Spec 002, edge case: an analyzer that throws is reported as a failure; one that hangs stops at the time limit and is named.</summary>
public sealed class AnalyzerDiagnosticsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Findings_failures_and_unfinished_analyzers_are_reported_apart()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(), VersionStamp.Default, "Sample", "Sample", LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            analyzerReferences:
            [
                new AnalyzerImageReference([new FlagsClasses()], display: "Flags"),
                new AnalyzerImageReference([new Throws()], display: "Throws"),
                new AnalyzerImageReference([new Hangs()], display: "Hangs"),
            ]));
        var document = workspace.AddDocument(project.Id, "Sample.cs", SourceText.From("public class Sample { }"));
        var compilation = (await document.Project.GetCompilationAsync(Ct))!;

        var run = await AnalyzerDiagnostics.ForTreesAsync(document.Project, compilation, [(await document.GetSyntaxTreeAsync(Ct))!], TimeSpan.FromSeconds(2), Ct);

        Assert.Equal("FAROL001", Assert.Single(run.Diagnostics).Id);
        Assert.Contains(run.Failures, f => f.Contains("Throws", StringComparison.Ordinal) && f.Contains("InvalidOperationException", StringComparison.Ordinal));
        Assert.Equal(["Hangs"], run.Unfinished);
        Assert.Contains("FAROL002", run.Uncovered);
        Assert.Contains("FAROL003", run.Uncovered);
        Assert.DoesNotContain("FAROL001", run.Uncovered);
    }

    [Fact]
    public async Task An_analyzer_that_ignores_cancellation_is_abandoned_soon_after_the_limit()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(), VersionStamp.Default, "Stuck", "Stuck", LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            analyzerReferences:
            [
                new AnalyzerImageReference([new FlagsClasses()], display: "Flags"),
                new AnalyzerImageReference([new IgnoresCancellation()], display: "Stuck"),
                new UnresolvedAnalyzerReference("missing/Gone.Analyzers.dll"),
            ]));
        var document = workspace.AddDocument(project.Id, "Sample.cs", SourceText.From("public class Sample { }"));
        var compilation = (await document.Project.GetCompilationAsync(Ct))!;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var run = await AnalyzerDiagnostics.ForTreesAsync(document.Project, compilation, [(await document.GetSyntaxTreeAsync(Ct))!], TimeSpan.FromSeconds(1), Ct);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(6), $"took {clock.Elapsed}");
        Assert.Equal("FAROL001", Assert.Single(run.Diagnostics).Id);
        Assert.Equal(["Stuck"], run.Unfinished);
        Assert.Contains(run.Failures, f => f.Contains("not found", StringComparison.Ordinal));
    }

#pragma warning disable RS1036, RS1026, RS2008, RS1038, RS1041 // test analyzers
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class FlagsClasses : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new("FAROL001", "Class", "Class {0}", "Test", DiagnosticSeverity.Warning, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterSymbolAction(c => c.ReportDiagnostic(Diagnostic.Create(Rule, c.Symbol.Locations[0], c.Symbol.Name)), SymbolKind.NamedType);
        }
    }

    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class Throws : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new("FAROL002", "Throws", "Throws", "Test", DiagnosticSeverity.Warning, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterSymbolAction(_ => throw new InvalidOperationException("broken analyzer"), SymbolKind.NamedType);
        }
    }

    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class Hangs : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new("FAROL003", "Hangs", "Hangs", "Test", DiagnosticSeverity.Warning, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterSymbolAction(c => Task.Delay(Timeout.Infinite, c.CancellationToken).Wait(c.CancellationToken), SymbolKind.NamedType);
        }
    }
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class IgnoresCancellation : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new("FAROL004", "Stuck", "Stuck", "Test", DiagnosticSeverity.Warning, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterSymbolAction(_ => Thread.Sleep(TimeSpan.FromSeconds(15)), SymbolKind.NamedType);
        }
    }
#pragma warning restore RS1036, RS1026, RS2008, RS1038, RS1041
}
