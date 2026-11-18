using System.Text;
using Farol.Core;
using Farol.Engine.CodeActions;
using Farol.Engine.Navigation;
using Farol.Engine.Workspaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;

namespace Farol.Engine.Refactoring;

/// <summary>A refactoring computed on a snapshot: its title, the file edits, the files it moves, and what it left as is.</summary>
public sealed record RefactoringResult(string Title, CodeActionChanges Changes, IReadOnlyList<SourceMove> Moves, IReadOnlyList<string> Notes)
{
    /// <summary>New source files of classic projects, with the project file the change makes list them.</summary>
    public IReadOnlyList<(string Path, string ProjectFile)> Added { get; init; } = [];
}

/// <summary>
/// Extract method: Roslyn's extract-method provider on whole lines of statements, then <see cref="Renamer"/> from the
/// name it generates to the requested one.
/// </summary>
public static class ExtractMethodRefactoring
{
    public static async Task<RefactoringResult> ComputeAsync(WorkspaceSnapshot snapshot, Document document, int startLine, int endLine, string name, string displayPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(document);

        var text = await document.GetTextAsync(cancellationToken);
        if (startLine < 1 || endLine < startLine || endLine > text.Lines.Count)
        {
            throw new FarolException(
                ErrorCodes.InvalidArgument,
                $"Lines {startLine}–{endLine} are not a range of {displayPath}, which has {text.Lines.Count} lines.",
                "Pass startLine ≤ endLine within the file, as dotnet_outline or dotnet_symbol report them.");
        }

        var span = Trimmed(text, TextSpan.FromBounds(text.Lines[startLine - 1].Start, text.Lines[endLine - 1].End));
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        var container = root is null || model is null ? null : Container(model, root.FindNode(span, getInnermostNodeForTie: true), cancellationToken);
        if (container is null)
        {
            throw new FarolException(
                ErrorCodes.InvalidArgument,
                $"Lines {startLine}–{endLine} of {displayPath} are not inside a method, property or constructor body.",
                "Pass lines that hold whole statements of one member.");
        }

        name = (name ?? string.Empty).Trim();
        RenameRefactoring.ValidateName(snapshot.Solution, container, name);
        var comparison = document.Project.Language == LanguageNames.VisualBasic ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (container.ContainingType?.GetMembers().FirstOrDefault(m => string.Equals(m.Name, name, comparison)) is { } existing)
        {
            throw new FarolException(
                ErrorCodes.Conflict,
                $"'{name}' already exists in {container.ContainingType.ToDisplayString(SymbolFormatter.Signature)}: {SymbolFormatter.Display(existing)}. Nothing was written.",
                "Pick another name.");
        }

        var action = await ExtractActionAsync(document, span, cancellationToken)
            ?? throw new FarolException(
                ErrorCodes.InvalidArgument,
                $"Roslyn cannot extract lines {startLine}–{endLine} of {displayPath} into a method{(document.Project.Language == LanguageNames.VisualBasic ? " (Visual Basic)" : string.Empty)}: the range must be whole statements of one block, without a return of only some paths or a jump out of it.",
                "Widen or narrow the range to whole statements; dotnet_code_actions on one line lists what Roslyn offers there.");

        var operations = await action.GetOperationsAsync(cancellationToken);
        if (operations.OfType<ApplyChangesOperation>().FirstOrDefault()?.ChangedSolution is not { } extracted)
        {
            throw new FarolException(ErrorCodes.InvalidArgument, $"Roslyn's extract method changed no file for lines {startLine}–{endLine} of {displayPath}.");
        }

        var renamed = await RenameGeneratedAsync(document, extracted, name, cancellationToken);
        var encoding = text.Encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var changes = await CodeActionEditor.ComputeAsync(snapshot, renamed, encoding, cancellationToken);
        return new RefactoringResult($"extract lines {startLine}–{endLine} of {displayPath} into {name}", changes, [], []);
    }

    // The member whose body holds the range: a method, an accessor, a constructor, a local function's owner.
    private static ISymbol? Container(SemanticModel model, SyntaxNode node, CancellationToken cancellationToken)
    {
        for (var current = node; current is not null; current = current.Parent)
        {
            if (model.GetDeclaredSymbol(current, cancellationToken) is { } declared and (IMethodSymbol { MethodKind: not MethodKind.LocalFunction and not MethodKind.AnonymousFunction } or IPropertySymbol or IEventSymbol))
            {
                return declared is IMethodSymbol { AssociatedSymbol: { } owner } ? owner : declared;
            }
        }

        return null;
    }

    private static async Task<CodeAction?> ExtractActionAsync(Document document, TextSpan span, CancellationToken cancellationToken)
    {
        foreach (var provider in CodeActionProviders.RefactoringsFor(document.Project.Language).Where(p => p.GetType().Name.Contains("ExtractMethod", StringComparison.Ordinal)))
        {
            var actions = new List<CodeAction>();
            try
            {
                await provider.ComputeRefactoringsAsync(new CodeRefactoringContext(document, span, actions.Add, cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031 // A provider that fails outside Visual Studio only means the range cannot be extracted.
            catch (Exception)
#pragma warning restore CA1031
            {
                continue;
            }

            if (actions.SelectMany(Leaves).FirstOrDefault(a => a is not CodeActionWithOptions && a.Title.StartsWith("Extract method", StringComparison.OrdinalIgnoreCase)) is { } extract)
            {
                return extract;
            }
        }

        return null;
    }

    private static IEnumerable<CodeAction> Leaves(CodeAction action) =>
        action.NestedActions.IsDefaultOrEmpty ? [action] : action.NestedActions.SelectMany(Leaves);

    /// <summary>The method the provider added (the one declared in the document now that was not before), renamed to <paramref name="name"/>.</summary>
    private static async Task<Solution> RenameGeneratedAsync(Document original, Solution extracted, string name, CancellationToken cancellationToken)
    {
        var before = await DeclaredAsync(original, cancellationToken);
        if (extracted.GetDocument(original.Id) is not { } changed)
        {
            return extracted;
        }

        var after = await DeclaredWithKeysAsync(changed, cancellationToken);
        var added = after.Where(a => !before.Contains(a.Key)).ToList();
        if (added.Count != 1)
        {
            return extracted;
        }

        var generated = added[0].Symbol;
        return string.Equals(generated.Name, name, StringComparison.Ordinal)
            ? extracted
            : await Renamer.RenameSymbolAsync(extracted, generated, new SymbolRenameOptions(), name, cancellationToken);
    }

    private static async Task<List<(ISymbol Symbol, string Key)>> DeclaredWithKeysAsync(Document document, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null)
        {
            return [];
        }

        return [.. root.DescendantNodes()
            .Select(n => model.GetDeclaredSymbol(n, cancellationToken))
            .OfType<IMethodSymbol>()
            .Where(m => m.MethodKind is MethodKind.Ordinary)
            .Distinct(SymbolEqualityComparer.Default)
            .Cast<IMethodSymbol>()
            .Select(m => ((ISymbol)m, Key(m)))];
    }

    private static async Task<HashSet<string>> DeclaredAsync(Document document, CancellationToken cancellationToken) =>
        [.. (await DeclaredWithKeysAsync(document, cancellationToken)).Select(d => d.Key)];

    private static string Key(ISymbol symbol) => SymbolFormatter.Id(symbol);

    private static TextSpan Trimmed(SourceText text, TextSpan span)
    {
        var start = span.Start;
        var end = span.End;
        while (start < end && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        while (end > start && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        return TextSpan.FromBounds(start, end);
    }
}
