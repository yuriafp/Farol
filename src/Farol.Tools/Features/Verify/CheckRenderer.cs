using System.Globalization;
using Farol.Core.Paths;
using Farol.Core.Text;
using Farol.Engine.Diagnostics;
using Farol.Engine.Workspaces;
using Farol.Tools.Infrastructure;
using Microsoft.CodeAnalysis;

namespace Farol.Tools.Features.Verify;

/// <summary>Check results as agents read them best: the verdict first, then one line per diagnostic grouped by project.</summary>
internal static class CheckRenderer
{
    private const int MaxMessageLength = 300;

    public static CheckOutput Render(ResponseBuilder text, CheckResult result, WorkspaceSnapshot snapshot, string root, CheckScope scope, int offset)
    {
        var baseline = $"baseline: the workspace as loaded {Ago(snapshot.Load.LoadedAt)}";
        CheckOutput Output(Page<DiagnosticItem> found, Page<DiagnosticItem>? existing) => new(
            scope.ToString().ToLowerInvariant(),
            result.New.Count(e => e.Severity == DiagnosticSeverity.Error),
            result.New.Count(e => e.Severity != DiagnosticSeverity.Error),
            snapshot.Load.LoadedAt.ToString("o", CultureInfo.InvariantCulture),
            [.. result.ChangedFiles.Select(f => DisplayPath.From(root, f))],
            result.CheckedProjects,
            [.. result.CheckedFiles.Select(f => DisplayPath.From(root, f))],
            [.. result.ChangedDuringLoad.Select(f => DisplayPath.From(root, f))],
            found,
            existing,
            null);

        if (scope == CheckScope.Changed && result.ChangedFiles.Count == 0)
        {
            text.Line($"no source files changed since the workspace loaded: nothing to check ({baseline}).");
            text.Line("hint: scope='solution' with includeExisting=true lists every diagnostic.");
            return Output(new Page<DiagnosticItem>(0, 0, null, []), null);
        }

        text.Line(result.New.Count == 0 ? "new since load: none — no errors or warnings introduced" : $"new since load: {Counts(result.New)}");
        text.Line(Checked(result, root, scope) + " · " + baseline);
        if (result.ChangedDuringLoad.Count > 0)
        {
            var files = string.Join(", ", result.ChangedDuringLoad.Take(5).Select(f => DisplayPath.From(root, f)));
            var more = result.ChangedDuringLoad.Count > 5 ? $" and {result.ChangedDuringLoad.Count - 5} more" : string.Empty;
            text.Line(
                $"changed while the workspace loaded: {files}{more} — Farol did not see these files before the change, so all of their errors " +
                "and warnings are listed as new; some may be older.");
        }

        var found = Page.From(result.New, Entries(text, result.New, root, offset), e => Item(e, root));
        Page<DiagnosticItem>? existing = null;
        if (result.Existing.Count > 0)
        {
            text.Line($"existing at load, still present: {Counts(result.Existing)}");
            existing = Page.From(result.Existing, Entries(text, result.Existing, root, 0), e => Item(e, root));
        }

        return Output(found, existing);
    }

    private static DiagnosticItem Item(DiagnosticEntry entry, string root) => new(
        entry.FilePath is null ? null : DisplayPath.Location(root, entry.FilePath, entry.Line),
        entry.Severity == DiagnosticSeverity.Error ? "error" : "warning",
        entry.Id,
        entry.Message.Length > MaxMessageLength ? string.Concat(entry.Message.AsSpan(0, MaxMessageLength), "…") : entry.Message,
        entry.Project,
        entry.TargetFrameworks);

    private static string Checked(CheckResult result, string root, CheckScope scope)
    {
        var parts = new List<string>();
        if (scope == CheckScope.Changed)
        {
            parts.Add($"changed: {result.ChangedFiles.Count} file(s)");
        }

        if (result.CheckedProjects.Count > 0)
        {
            parts.Add($"checked projects: {string.Join(", ", result.CheckedProjects.Take(12))}{(result.CheckedProjects.Count > 12 ? $" and {result.CheckedProjects.Count - 12} more" : string.Empty)}");
        }

        if (result.CheckedFiles.Count > 0)
        {
            var files = string.Join(", ", result.CheckedFiles.Take(5).Select(f => DisplayPath.From(root, f)));
            var more = result.CheckedFiles.Count > 5 ? $" and {result.CheckedFiles.Count - 5} more" : string.Empty;
            parts.Add(scope == CheckScope.Changed ? $"checked files (edited, and those using changed declarations): {files}{more}" : $"checked: {files}{more}");
        }

        return string.Join(" · ", parts);
    }

    private static ListWindow Entries(ResponseBuilder text, IReadOnlyList<DiagnosticEntry> entries, string root, int offset)
    {
        var start = Math.Clamp(offset, 0, entries.Count);
        string? project = null;
        var index = start;
        for (; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (!string.Equals(project, entry.Project, StringComparison.Ordinal))
            {
                if (!text.TryLine($"{entry.Project}:"))
                {
                    break;
                }

                project = entry.Project;
            }

            if (!text.TryLine(Line(entry, root)))
            {
                break;
            }
        }

        text.More(entries.Count - index, ResponseBuilder.OffsetHint(index));
        return new ListWindow(start, index, entries.Count);
    }

    private static string Line(DiagnosticEntry entry, string root)
    {
        var where = entry.FilePath is null ? "(project)" : DisplayPath.Location(root, entry.FilePath, entry.Line);
        var severity = entry.Severity == DiagnosticSeverity.Error ? "error" : "warning";
        var message = entry.Message.Length > MaxMessageLength ? string.Concat(entry.Message.AsSpan(0, MaxMessageLength), "…") : entry.Message;
        var frameworks = entry.TargetFrameworks.Count > 0 ? $" · {string.Join(", ", entry.TargetFrameworks)}" : string.Empty;
        return $"- {where} · {severity} {entry.Id} · {message}{frameworks}";
    }

    private static string Counts(IReadOnlyList<DiagnosticEntry> entries)
    {
        var errors = entries.Count(e => e.Severity == DiagnosticSeverity.Error);
        var warnings = entries.Count - errors;
        return (errors, warnings) switch
        {
            (> 0, > 0) => $"{errors} error(s), {warnings} warning(s)",
            (> 0, _) => $"{errors} error(s)",
            _ => $"{warnings} warning(s)",
        };
    }

    private static string Ago(DateTimeOffset moment)
    {
        var elapsed = DateTimeOffset.UtcNow - moment;
        return elapsed.TotalMinutes < 1 ? "less than a minute ago"
            : elapsed.TotalHours < 1 ? $"{(int)elapsed.TotalMinutes} min ago"
            : $"{elapsed.TotalHours.ToString("0.#", CultureInfo.InvariantCulture)} h ago";
    }
}

/// <summary>
/// Structured output of dotnet_check: the diagnostics the text lists. With format=hook the text is the hook's JSON and
/// <see cref="Hook"/> holds it too.
/// </summary>
public sealed record CheckOutput(
    string Scope,
    int NewErrors,
    int NewWarnings,
    string? BaselineLoadedAt,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> CheckedProjects,
    IReadOnlyList<string> CheckedFiles,
    IReadOnlyList<string> ChangedDuringLoad,
    Page<DiagnosticItem> New,
    Page<DiagnosticItem>? Existing,
    string? Hook,
    [property: System.ComponentModel.Description("False when nothing was checked (a hook answer while the workspace loads, or for a file Farol does not check): the empty lists then say nothing.")] bool Checked = true)
{
    /// <summary>A hook answer given before anything was checked: an empty result that carries the hook's JSON.</summary>
    public static CheckOutput ForHook(string json) => new("changed", 0, 0, null, [], [], [], [], new Page<DiagnosticItem>(0, 0, null, []), null, json, Checked: false);
}

public sealed record DiagnosticItem(string? Location, string Severity, string Id, string Message, string Project, IReadOnlyList<string> TargetFrameworks);
