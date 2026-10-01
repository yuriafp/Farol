using System.Text;
using System.Text.RegularExpressions;
using Farol.Core;
using Farol.Core.Files;
using Farol.Core.Paths;
using Farol.Engine.Workspaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.Text;

namespace Farol.Engine.CodeActions;

/// <summary>One file an action creates, changes or deletes; a null text means the file does not exist on that side.</summary>
public sealed record FileEdit(string FilePath, string? OldText, string? NewText, Encoding Encoding, string Project, bool InSdkStyleProject);

/// <summary>What an action would do: source file edits, plus the changes Farol does not apply (references, projects).</summary>
public sealed record CodeActionChanges(IReadOnlyList<FileEdit> Files, IReadOnlyList<string> NotApplied);

/// <summary>Turns a code action into file edits (for a diff) and writes them safely (for apply).</summary>
public static partial class CodeActionEditor
{
    public static async Task<CodeActionChanges> ComputeAsync(WorkspaceSnapshot snapshot, CodeAction action, Document trigger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(trigger);

        var operations = await action.GetOperationsAsync(cancellationToken);
        if (operations.OfType<ApplyChangesOperation>().FirstOrDefault() is not { } apply)
        {
            return new CodeActionChanges([], ["it changes no files (it needs an interactive editor)"]);
        }

        var original = snapshot.Solution;
        var changed = apply.ChangedSolution;
        var encoding = (await trigger.GetTextAsync(cancellationToken)).Encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var files = new Dictionary<string, FileEdit>(StringComparer.OrdinalIgnoreCase);
        var notApplied = new List<string>();
        var changes = changed.GetChanges(original);
        notApplied.AddRange(changes.GetAddedProjects().Select(p => $"adds project {p.Name}"));
        notApplied.AddRange(changes.GetRemovedProjects().Select(p => $"removes project {p.Name}"));
        foreach (var project in changes.GetProjectChanges())
        {
            var entry = snapshot.Projects.Get(project.ProjectId);
            var name = entry?.Name ?? project.NewProject.Name;
            var sdkStyle = entry?.IsSdkStyle ?? true;
            foreach (var id in project.GetChangedDocuments(onlyGetDocumentsWithTextChanges: true))
            {
                var before = original.GetDocument(id)!;
                if (before.FilePath is { } path && !files.ContainsKey(path))
                {
                    var oldText = await before.GetTextAsync(cancellationToken);
                    var newText = await KeepLineBreaksAsync(before, changed.GetDocument(id)!, oldText, cancellationToken);
                    files[path] = new FileEdit(path, oldText.ToString(), newText, oldText.Encoding ?? encoding, name, sdkStyle);
                }
            }

            foreach (var id in project.GetAddedDocuments())
            {
                var document = changed.GetDocument(id)!;
                var path = document.FilePath
                    ?? (Path.GetDirectoryName(project.NewProject.FilePath) is { } directory ? Path.Combine([directory, .. document.Folders, document.Name]) : null);
                if (path is not null && !files.ContainsKey(path))
                {
                    files[path] = new FileEdit(path, null, (await document.GetTextAsync(cancellationToken)).ToString(), encoding, name, sdkStyle);
                }
            }

            foreach (var id in project.GetRemovedDocuments())
            {
                var document = original.GetDocument(id)!;
                if (document.FilePath is { } path && !files.ContainsKey(path))
                {
                    files[path] = new FileEdit(path, (await document.GetTextAsync(cancellationToken)).ToString(), null, encoding, name, sdkStyle);
                }
            }

            if (project.GetAddedProjectReferences().Any() || project.GetRemovedProjectReferences().Any()
                || project.GetAddedMetadataReferences().Any() || project.GetRemovedMetadataReferences().Any())
            {
                notApplied.Add($"changes the references of {name}");
            }

            if (project.GetAddedAdditionalDocuments().Any() || project.GetChangedAdditionalDocuments().Any() || project.GetRemovedAdditionalDocuments().Any()
                || project.GetAddedAnalyzerConfigDocuments().Any() || project.GetChangedAnalyzerConfigDocuments().Any() || project.GetRemovedAnalyzerConfigDocuments().Any())
            {
                notApplied.Add($"changes non-source files of {name}");
            }
        }

        return new CodeActionChanges([.. files.Values.OrderBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase)], [.. notApplied.Distinct(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Writes the edits after vetting all of them: inside the trusted directories, unchanged on disk since the
    /// snapshot, and not adding or removing files in classic projects (their project file lists every source file).
    /// Returns the paths written.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ApplyAsync(IReadOnlyList<FileEdit> files, PathSandbox paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(paths);

        foreach (var file in files)
        {
            var display = DisplayPath.From(paths.Root, file.FilePath);
            paths.Demand(file.FilePath, $"File '{display}'");
            if ((file.OldText is null || file.NewText is null) && !file.InSdkStyleProject)
            {
                throw new FarolException(
                    ErrorCodes.InvalidArgument,
                    $"The action {(file.OldText is null ? "adds" : "removes")} '{display}' in {file.Project}, a classic project whose project file lists every source file.",
                    "Apply it by hand: take the diff, then add or remove the matching <Compile Include=\"...\" /> item in the project file.");
            }

            var onDisk = File.Exists(file.FilePath) ? await File.ReadAllTextAsync(file.FilePath, file.Encoding, cancellationToken) : null;
            if (!string.Equals(onDisk, file.OldText, StringComparison.Ordinal))
            {
                throw new FarolException(
                    ErrorCodes.Conflict,
                    file.OldText is null ? $"'{display}' already exists." : $"'{display}' changed on disk while the action was computed.",
                    "Call dotnet_code_actions again: it computes the action on the current content.");
            }
        }

        var written = new List<string>();
        foreach (var file in files)
        {
            try
            {
                if (file.NewText is null)
                {
                    File.Delete(file.FilePath);
                }
                else
                {
                    await AtomicFile.WriteAllTextAsync(file.FilePath, file.NewText, file.Encoding, cancellationToken);
                }

                written.Add(file.FilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var done = written.Count == 0 ? string.Empty : $" Already written: {string.Join(", ", written.Select(p => DisplayPath.From(paths.Root, p)))}.";
                throw new FarolException(
                    ErrorCodes.WriteFailed,
                    $"Could not write '{DisplayPath.From(paths.Root, file.FilePath)}': {ex.Message}{done}",
                    "Check that the file is not read-only or locked by another program, then call again.");
            }
        }

        return written;
    }

    // Generated code uses the host's default line break; keep the file's own, so the diff and git show only the real change.
    private static async Task<string> KeepLineBreaksAsync(Document before, Document after, SourceText oldText, CancellationToken cancellationToken)
    {
        var changes = await after.GetTextChangesAsync(before, cancellationToken);
        if (DominantLineBreak(oldText) is not { } lineBreak)
        {
            return oldText.WithChanges(changes).ToString();
        }

        return oldText.WithChanges(changes.Select(c => new TextChange(c.Span, LineBreak().Replace(c.NewText ?? string.Empty, lineBreak)))).ToString();
    }

    private static string? DominantLineBreak(SourceText text)
    {
        int crlf = 0, lf = 0;
        foreach (var line in text.Lines)
        {
            switch (line.EndIncludingLineBreak - line.End)
            {
                case 2:
                    crlf++;
                    break;
                case 1 when text[line.End] == '\n':
                    lf++;
                    break;
            }
        }

        return crlf == 0 && lf == 0 ? null : crlf >= lf ? "\r\n" : "\n";
    }

    [GeneratedRegex("\r\n|\r|\n")]
    private static partial Regex LineBreak();
}
