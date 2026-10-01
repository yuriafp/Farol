using System.Globalization;
using Farol.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Text;

namespace Farol.Engine.CodeActions;

/// <summary>A compiler diagnostic on the requested line.</summary>
public sealed record LineDiagnostic(string Id, DiagnosticSeverity Severity, string Message);

/// <summary>One action an agent can preview or apply. Fixes name the diagnostic they fix.</summary>
public sealed record CodeActionItem(int Number, string Title, string Kind, string? DiagnosticId, string Provider, CodeAction Action);

public sealed record CodeActionList(IReadOnlyList<LineDiagnostic> Diagnostics, IReadOnlyList<CodeActionItem> Actions);

/// <summary>
/// The lightbulb at one line: the code fixes for the diagnostics on it, then the refactorings available there.
/// Nested actions are flattened; actions that need a dialog (<see cref="CodeActionWithOptions"/>) are left out.
/// </summary>
public static class CodeActionFinder
{
    public static async Task<CodeActionList> ListAsync(Document document, int line, int? column, string? diagnosticId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        var text = await document.GetTextAsync(cancellationToken);
        if (line < 1 || line > text.Lines.Count)
        {
            throw new FarolException(ErrorCodes.InvalidArgument, $"Line {line} is outside the file ({text.Lines.Count} lines).");
        }

        var lineSpan = text.Lines[line - 1].Span;
        var model = await document.GetSemanticModelAsync(cancellationToken);
        var diagnostics = (model?.GetDiagnostics(cancellationToken: cancellationToken) ?? [])
            .Where(d => !d.IsSuppressed && OnLine(d, line - 1))
            .Where(d => diagnosticId is null || d.Id.Equals(diagnosticId.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();

        var found = new List<(string Title, string Kind, string? DiagnosticId, string Provider, CodeAction Action)>();
        foreach (var diagnostic in diagnostics)
        {
            foreach (var provider in CodeActionProviders.FixersFor(document.Project.Language, diagnostic.Id))
            {
                var actions = new List<CodeAction>();
                if (await TryAsync(() => provider.RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), cancellationToken)), cancellationToken))
                {
                    found.AddRange(actions.SelectMany(Leaves).Select(a => (a.Title, "fix", (string?)diagnostic.Id, CodeActionProviders.Label(provider), a)));
                }
            }
        }

        if (diagnosticId is null)
        {
            var span = column is { } c ? new TextSpan(lineSpan.Start + Math.Clamp(c - 1, 0, lineSpan.Length), 0) : Trimmed(text, lineSpan);
            foreach (var provider in CodeActionProviders.RefactoringsFor(document.Project.Language))
            {
                var actions = new List<CodeAction>();
                if (await TryAsync(() => provider.ComputeRefactoringsAsync(new CodeRefactoringContext(document, span, actions.Add, cancellationToken)), cancellationToken))
                {
                    found.AddRange(actions.SelectMany(Leaves).Select(a => (a.Title, "refactoring", (string?)null, CodeActionProviders.Label(provider), a)));
                }
            }
        }

        // Fixes before refactorings, then Roslyn's own priority (add using ranks above generate type), as the lightbulb shows them.
        var items = found
            .Where(f => f.Action is not CodeActionWithOptions)
            .OrderBy(f => f.Kind == "fix" ? 0 : 1)
            .ThenByDescending(f => f.Action.Priority)
            .DistinctBy(f => f.Title, StringComparer.Ordinal)
            .Select((f, index) => new CodeActionItem(index + 1, f.Title, f.Kind, f.DiagnosticId, f.Provider, f.Action))
            .ToList();
        var shown = diagnostics
            .Where(d => d.Severity != DiagnosticSeverity.Hidden)
            .Select(d => new LineDiagnostic(d.Id, d.Severity, d.GetMessage(CultureInfo.InvariantCulture)))
            .Distinct()
            .ToList();
        return new CodeActionList(shown, items);
    }

    /// <summary>Picks an action by its number in the list or by its title (exact, then case-insensitive, then a unique substring).</summary>
    public static CodeActionItem Select(CodeActionList list, string action)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        var wanted = action.Trim().TrimStart('#');
        if (int.TryParse(wanted, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && list.Actions.FirstOrDefault(a => a.Number == number) is { } byNumber)
        {
            return byNumber;
        }

        var matches = list.Actions.Where(a => a.Title.Equals(wanted, StringComparison.Ordinal)).ToList();
        if (matches.Count == 0)
        {
            matches = [.. list.Actions.Where(a => a.Title.Equals(wanted, StringComparison.OrdinalIgnoreCase))];
        }

        if (matches.Count == 0)
        {
            matches = [.. list.Actions.Where(a => a.Title.Contains(wanted, StringComparison.OrdinalIgnoreCase))];
        }

        return matches.Count == 1
            ? matches[0]
            : throw new FarolException(
                ErrorCodes.InvalidArgument,
                matches.Count == 0 ? $"No code action matches '{action}' here." : $"'{action}' matches {matches.Count} code actions.",
                "Call without 'action' to list them, then pass a number or an exact title.");
    }

    private static IEnumerable<CodeAction> Leaves(CodeAction action) =>
        action.NestedActions.IsDefaultOrEmpty ? [action] : action.NestedActions.SelectMany(Leaves);

    private static bool OnLine(Diagnostic diagnostic, int lineIndex)
    {
        if (!diagnostic.Location.IsInSource)
        {
            return false;
        }

        var span = diagnostic.Location.GetLineSpan();
        return span.StartLinePosition.Line <= lineIndex && lineIndex <= span.EndLinePosition.Line;
    }

    // Without a column the whole statement on the line is the selection, which is what extract/introduce refactorings need.
    private static TextSpan Trimmed(SourceText text, TextSpan line)
    {
        var start = line.Start;
        var end = line.End;
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

    // A provider that fails outside Visual Studio (missing host service, unexpected syntax) only loses its own actions.
    private static async Task<bool> TryAsync(Func<Task> register, CancellationToken cancellationToken)
    {
        try
        {
            await register();
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Third-party-style provider code: any failure is isolated to that provider.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }
}
