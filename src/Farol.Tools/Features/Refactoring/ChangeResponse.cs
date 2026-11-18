using System.Collections.Concurrent;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using Farol.Core;
using Farol.Core.Paths;
using Farol.Core.Text;
using Farol.Engine.CodeActions;
using Farol.Engine.Diagnostics;
using Farol.Engine.Workspaces;
using Farol.Tools.Features.Verify;
using Farol.Tools.Infrastructure;

namespace Farol.Tools.Features.Refactoring;

/// <summary>What a write tool was asked for: the change, how to name it, and where it came from.</summary>
internal sealed record ChangeRequest(
    string Tool,
    string Title,
    string PreviewKey,
    CodeActionChanges Changes,
    IReadOnlyList<SourceMove> Moves,
    IReadOnlyList<string> Notes,
    bool Apply)
{
    /// <summary>For a rename: the old and new names, so later checks read the load's diagnostics with the new name.</summary>
    public (string Old, string New)? Renamed { get; init; }
}

/// <summary>
/// The path every write tool shares: a unified diff that changes nothing, or, with apply, the files written (after
/// checking that none changed since the diff was shown) and a fresh dotnet_check.
/// </summary>
internal static class ChangeResponse
{
    private const int MaxPreviews = 64;

    // The files each preview showed, by tool and arguments, as hashes of their content then: an apply of the same call
    // refuses when one of them has changed since, instead of writing a change the agent never saw.
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> Previews = new(StringComparer.Ordinal);

    public static async Task<ToolOutput<ChangeOutput>> RespondAsync(
        ChangeRequest request, WorkspaceSession session, WorkspaceManager workspaces, ResponseBuilder text, CancellationToken cancellationToken)
    {
        var root = workspaces.RootDirectory;
        var changes = request.Changes;
        var moves = request.Moves.Select(m => new MoveItem(DisplayPath.From(root, m.From), DisplayPath.From(root, m.To))).ToList();
        var files = Files(changes, request.Moves, root);
        if (changes.Files.Count == 0)
        {
            text.Line($"{request.Title}: nothing to change.");
            Notes(text, request.Notes, changes.NotApplied);
            return ToolOutput.Of(text.ToString(), new ChangeOutput(request.Title, "nothing", files, moves, request.Notes, changes.NotApplied, null, null));
        }

        var key = $"{session.Target.Path}|{request.PreviewKey}";
        var shown = Fingerprint(changes);
        if (!request.Apply)
        {
            if (Previews.Count >= MaxPreviews)
            {
                Previews.Clear();
            }

            Previews[key] = shown;
            text.Line($"preview of {request.Title}: {changes.Files.Count} file(s), diff only — no file was changed");
            Moved(text, moves);
            Notes(text, request.Notes, changes.NotApplied);
            var preview = Diff(text, changes, root);
            text.Line("to write it: call again with the same arguments and apply=true.");
            return ToolOutput.Of(text.ToString(), new ChangeOutput(request.Title, "preview", files, moves, request.Notes, changes.NotApplied, preview, null));
        }

        if (changes.NotApplied.Count > 0)
        {
            throw new FarolException(
                ErrorCodes.InvalidArgument,
                $"{request.Title} also {string.Join("; ", changes.NotApplied)}, and Farol only applies source file edits.",
                "Take the diff (call without apply) and make the other changes by hand.");
        }

        if (Previews.TryRemove(key, out var previewed) && Changed(previewed, shown) is { Count: > 0 } changed)
        {
            throw new FarolException(
                ErrorCodes.Conflict,
                $"{string.Join(", ", changed.Select(p => DisplayPath.From(root, p)))} changed since the diff was shown, so the change would not be the one previewed. Nothing was written.",
                $"Call {request.Tool} again without apply to see the change on the current content.");
        }

        if (request.Moves.Count > 0)
        {
            // Before writing: the watcher sees a project file as soon as it is written.
            session.ExpectProjectWrites(changes.Files
                .Where(f => f.NewText is not null && Path.GetExtension(f.FilePath).EndsWith("proj", StringComparison.OrdinalIgnoreCase))
                .Select(f => (f.FilePath, f.NewText!)));
        }

        // Before writing too: a snapshot taken in between must still move the documents, not drop the old file's.
        session.NotifyMoved(request.Moves);
        IReadOnlyList<string> written;
        try
        {
            written = await CodeActionEditor.ApplyAsync(changes.Files, workspaces.Paths, request.Tool, cancellationToken);
        }
        catch
        {
            session.ForgetMoves(request.Moves);
            throw;
        }

        if (request.Renamed is { } renamed)
        {
            session.NotifyRenamed(renamed.Old, renamed.New);
        }

        session.NotifyChanged(written);
        var updated = await session.GetSnapshotAsync(wait: true, cancellationToken);
        var check = await DiagnosticCheck.RunAsync(updated, new CheckRequest(CheckScope.Changed), cancellationToken);

        text.Line($"applied {request.Title}: wrote {string.Join(", ", written.Select(p => DisplayPath.From(root, p)))}");
        Moved(text, moves);
        Notes(text, request.Notes, []);
        text.Line("check after applying:");
        var checkOutput = CheckRenderer.Render(text, check, updated, root, CheckScope.Changed, offset: 0);
        text.Line("diff written:");
        var diff = Diff(text, changes, root);
        return ToolOutput.Of(text.ToString(), new ChangeOutput(request.Title, "applied", files, moves, request.Notes, [], diff, checkOutput));
    }

    /// <summary>Writes the diff as far as the budget allows; returns the lines written, joined.</summary>
    public static string Diff(ResponseBuilder text, CodeActionChanges changes, string root)
    {
        var lines = changes.Files
            .SelectMany(f => UnifiedDiff.Create(DisplayPath.From(root, f.FilePath), f.OldText, f.NewText).Split('\n'))
            .ToList();
        var written = 0;
        foreach (var line in lines)
        {
            if (!text.TryLine(line))
            {
                break;
            }

            written++;
        }

        text.More(lines.Count - written, "diff lines; raise maxTokens to see all");
        return string.Join('\n', lines.Take(written));
    }

    private static void Moved(ResponseBuilder text, List<MoveItem> moves)
    {
        foreach (var move in moves)
        {
            text.Line($"renames {move.From} → {move.To}");
        }
    }

    private static void Notes(ResponseBuilder text, IReadOnlyList<string> notes, IReadOnlyList<string> notApplied)
    {
        foreach (var note in notes)
        {
            text.Line($"note: {note}");
        }

        if (notApplied.Count > 0)
        {
            text.Line($"note: it also {string.Join("; ", notApplied)} (not shown, and not applied by Farol)");
        }
    }

    private static List<ChangedFileItem> Files(CodeActionChanges changes, IReadOnlyList<SourceMove> moves, string root) =>
        [.. changes.Files.Select(f => new ChangedFileItem(
            DisplayPath.From(root, f.FilePath),
            f.OldText is null ? "created" : f.NewText is null ? "deleted" : "edited",
            f.Project))];

    private static Dictionary<string, string> Fingerprint(CodeActionChanges changes) =>
        changes.Files.ToDictionary(f => f.FilePath, f => Hash(f.OldText), StringComparer.OrdinalIgnoreCase);

    private static List<string> Changed(IReadOnlyDictionary<string, string> previewed, IReadOnlyDictionary<string, string> now) =>
        [.. previewed.Keys.Union(now.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(path => !previewed.TryGetValue(path, out var before) || !now.TryGetValue(path, out var after) || before != after)
            .Order(StringComparer.OrdinalIgnoreCase)];

    private static string Hash(string? text) => text is null ? "-" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

/// <summary>
/// Structured output of a write tool. <see cref="Mode"/> is preview, applied or nothing; <see cref="Diff"/> is the diff
/// as far as the text shows it; <see cref="Check"/> follows an apply.
/// </summary>
public sealed record ChangeOutput(
    string Change,
    [property: Description("preview (nothing written), applied, or nothing (no file to change).")] string Mode,
    IReadOnlyList<ChangedFileItem> Files,
    IReadOnlyList<MoveItem> Moves,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> NotApplied,
    string? Diff,
    CheckOutput? Check);

public sealed record ChangedFileItem(string Path, [property: Description("edited, created or deleted.")] string Change, string Project);

public sealed record MoveItem(string From, string To);
