using System.ComponentModel;
using Farol.Core;
using Farol.Core.Paths;
using Farol.Core.Text;
using Farol.Engine.CodeActions;
using Farol.Engine.Diagnostics;
using Farol.Engine.Workspaces;
using Farol.Tools.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Farol.Tools.Features.Verify;

[McpServerToolType]
public sealed partial class CodeActionsTool(WorkspaceManager workspaces, ServerPermissions permissions, CallerContext caller, ILogger<CodeActionsTool> logger)
{
    [McpServerTool(Name = "dotnet_code_actions", Title = "C#/VB code fixes and refactorings", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(CodeActionsOutput))]
    [Description(
        "The Visual Studio lightbulb for one line of C#/VB code: compiler fixes (add a missing using/Imports, generate a member, implement an interface…) " +
        "and refactorings (extract method, introduce variable…). Without 'action' it lists them; with 'action' it returns the change as a unified diff " +
        "and changes no file; with apply=true it also writes the files and returns a fresh dotnet_check.")]
    public Task<CallToolResult> Run(
        [Description("Source file, relative to the root as responses write paths (e.g. src/App/Orders/OrderService.cs).")] string path,
        [Description("1-based line of the diagnostic or code.")] int line,
        [Description("Optional 1-based column: refactorings for the code at that position instead of the whole line.")] int? column = null,
        [Description("Only fixes for this diagnostic id, e.g. CS0246 or BC30451.")] string? diagnosticId = null,
        [Description("The number or exact title of an action from the list, to preview it as a diff (or apply it).")] string? action = null,
        [Description("Write the action's change to disk and return a fresh check. Needs 'action'; refused when Farol runs with --read-only.")] bool apply = false,
        [McpHeader(ToolParameters.WorkspaceHeader), Description(ToolParameters.WorkspaceDescription)] string? workspace = null,
        [Description(ToolParameters.MaxTokensDescription)] int maxTokens = TokenBudget.DefaultTokens,
        CancellationToken cancellationToken = default) =>
        ToolGuard.RunAsync(async () =>
        {
            if (apply)
            {
                permissions.Demand("write files", "Call again without apply=true to get the change as a diff.");
                if (string.IsNullOrWhiteSpace(action))
                {
                    throw new FarolException(ErrorCodes.InvalidArgument, "apply=true needs 'action'.", "List the actions first, then pass the number or title of one.");
                }
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                throw new FarolException(ErrorCodes.InvalidArgument, "'path' is empty.", "Pass the source file, relative to the root as responses write paths (e.g. src/App/Orders/OrderService.cs).");
            }

            var root = workspaces.RootDirectory;
            var session = workspaces.GetSession(workspace, path);
            var fullPath = workspaces.Paths.Resolve(path, session.Target.Directory);
            var snapshot = await session.GetSnapshotAsync(wait: true, cancellationToken);
            var document = snapshot.Solution.GetDocument(SourceDocuments.Find(snapshot, fullPath, path.Trim())[0])!;
            var list = await CodeActionFinder.ListAsync(document, line, column, diagnosticId, cancellationToken);
            var where = DisplayPath.Location(root, fullPath, line);
            var text = new ResponseBuilder(maxTokens);
            var diagnostics = list.Diagnostics
                .Select(d => new CodeActionDiagnosticItem(d.Severity == DiagnosticSeverity.Error ? "error" : d.Severity.ToString().ToLowerInvariant(), d.Id, d.Message))
                .ToList();
            if (string.IsNullOrWhiteSpace(action))
            {
                var actions = RenderList(text, list, where);
                return ToolOutput.Of(text.ToString(), new CodeActionsOutput(where, "list", diagnostics, actions, null, [], [], null, null));
            }

            var item = CodeActionFinder.Select(list, action);
            var chosen = Item(item);
            var changes = await CodeActionEditor.ComputeAsync(snapshot, item.Action, document, cancellationToken);
            if (changes.Files.Count == 0)
            {
                text.Line($"'{item.Title}' at {where} {string.Join("; ", changes.NotApplied)}: nothing to show or apply.");
                return ToolOutput.Of(text.ToString(), new CodeActionsOutput(where, "nothing", diagnostics, null, chosen, [], changes.NotApplied, null, null));
            }

            var files = changes.Files.Select(f => DisplayPath.From(root, f.FilePath)).ToList();
            if (!apply)
            {
                text.Line($"preview of {Describe(item)} at {where}: {changes.Files.Count} file(s), diff only — no file was changed");
                Notes(text, changes);
                var preview = Diff(text, changes, root);
                text.Line("to write it: call again with the same arguments and apply=true.");
                return ToolOutput.Of(text.ToString(), new CodeActionsOutput(where, "preview", diagnostics, null, chosen, files, changes.NotApplied, preview, null));
            }

            if (changes.NotApplied.Count > 0)
            {
                throw new FarolException(
                    ErrorCodes.InvalidArgument,
                    $"'{item.Title}' also {string.Join("; ", changes.NotApplied)}, and Farol only applies source file edits.",
                    "Apply it in an IDE, or take the diff (call without apply) and make the other changes by hand.");
            }

            var written = await CodeActionEditor.ApplyAsync(changes.Files, workspaces.Paths, cancellationToken);
            LogApplied(logger, item.Title, written.Count, caller.User, caller.Origin);
            session.NotifyChanged(written);
            var updated = await session.GetSnapshotAsync(wait: true, cancellationToken);
            var check = await DiagnosticCheck.RunAsync(updated, new CheckRequest(CheckScope.Changed), cancellationToken);

            text.Line($"applied {Describe(item)} at {where}: wrote {string.Join(", ", written.Select(p => DisplayPath.From(root, p)))}");
            text.Line("check after applying:");
            var checkOutput = CheckRenderer.Render(text, check, updated, root, CheckScope.Changed, offset: 0);
            text.Line("diff written:");
            var diff = Diff(text, changes, root);
            return ToolOutput.Of(text.ToString(), new CodeActionsOutput(
                where, "applied", diagnostics, null, chosen, [.. written.Select(p => DisplayPath.From(root, p))], [], diff, checkOutput));
        });

    private static CodeActionItemOutput Item(CodeActionItem item) =>
        new(item.Number, item.Title, item.DiagnosticId is null ? "refactoring" : "fix", item.DiagnosticId, item.Provider);

    private static Page<CodeActionItemOutput> RenderList(ResponseBuilder text, CodeActionList list, string where)
    {
        foreach (var diagnostic in list.Diagnostics)
        {
            text.Line($"{(diagnostic.Severity == DiagnosticSeverity.Error ? "error" : diagnostic.Severity.ToString().ToLowerInvariant())} {diagnostic.Id} at {where}: {diagnostic.Message}");
        }

        if (list.Actions.Count == 0)
        {
            text.Line($"no code actions at {where}.");
            return new Page<CodeActionItemOutput>(0, 0, null, []);
        }

        text.Line($"code actions at {where} ({list.Actions.Count}):");
        var written = 0;
        foreach (var item in list.Actions)
        {
            if (!text.TryLine($"{item.Number}. {item.Title} · {Kind(item)}"))
            {
                break;
            }

            written++;
        }

        text.More(list.Actions.Count - written, "raise maxTokens, or pass diagnosticId or column to narrow the list");
        text.Line("preview one as a diff: call again with action=<number or title>; add apply=true to write it.");
        return new Page<CodeActionItemOutput>(list.Actions.Count, 0, written < list.Actions.Count ? written : null, [.. list.Actions.Take(written).Select(Item)]);
    }

    private static void Notes(ResponseBuilder text, CodeActionChanges changes)
    {
        if (changes.NotApplied.Count > 0)
        {
            text.Line($"note: the action also {string.Join("; ", changes.NotApplied)} (not shown, and not applied by Farol)");
        }
    }

    // Returns the diff lines written, joined: the diff as far as the text shows it.
    private static string Diff(ResponseBuilder text, CodeActionChanges changes, string root)
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

    private static string Describe(CodeActionItem item) => $"'{item.Title}' ({Kind(item)})";

    private static string Kind(CodeActionItem item) =>
        item.DiagnosticId is null ? $"refactoring · {item.Provider}" : $"fix {item.DiagnosticId} · {item.Provider}";

    [LoggerMessage(Level = LogLevel.Information, Message = "Applied code action '{Action}' to {Files} file(s) for {User} via {Origin}")]
    private static partial void LogApplied(ILogger logger, string action, int files, string user, string origin);
}

/// <summary>
/// Structured output of dotnet_code_actions. <see cref="Mode"/> is list, preview, applied or nothing (an action with no
/// source change); <see cref="Diff"/> is the diff as far as the text shows it; <see cref="Check"/> follows an apply.
/// </summary>
public sealed record CodeActionsOutput(
    string Location,
    string Mode,
    IReadOnlyList<CodeActionDiagnosticItem> Diagnostics,
    Page<CodeActionItemOutput>? Actions,
    CodeActionItemOutput? Action,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> NotApplied,
    string? Diff,
    CheckOutput? Check);

public sealed record CodeActionDiagnosticItem(string Severity, string Id, string Message);

public sealed record CodeActionItemOutput(int Number, string Title, string Kind, string? DiagnosticId, string Provider);
