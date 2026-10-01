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
using ModelContextProtocol.Server;

namespace Farol.Tools.Features.Verify;

[McpServerToolType]
public sealed partial class CodeActionsTool(WorkspaceManager workspaces, ServerPermissions permissions, CallerContext caller, ILogger<CodeActionsTool> logger)
{
    [McpServerTool(Name = "dotnet_code_actions", Title = "C#/VB code fixes and refactorings", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(
        "The Visual Studio lightbulb for one line of C#/VB code: compiler fixes (add a missing using/Imports, generate a member, implement an interface…) " +
        "and refactorings (extract method, introduce variable…). Without 'action' it lists them; with 'action' it returns the change as a unified diff " +
        "and changes no file; with apply=true it also writes the files and returns a fresh dotnet_check.")]
    public Task<string> Run(
        [Description("Source file, relative to the workspace root (e.g. src/App/Orders/OrderService.cs).")] string path,
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

            var root = workspaces.RootDirectory;
            var fullPath = workspaces.Paths.Resolve(path);
            var session = workspaces.GetSession(workspace);
            var snapshot = await session.GetSnapshotAsync(wait: true, cancellationToken);
            var ids = snapshot.Solution.GetDocumentIdsWithFilePath(fullPath);
            if (ids.IsEmpty)
            {
                throw new FarolException(ErrorCodes.InvalidArgument, $"'{path}' is not a source file of the workspace.", "Use a path relative to the workspace root, as returned by other dotnet_* tools.");
            }

            var document = snapshot.Solution.GetDocument(ids[0])!;
            var list = await CodeActionFinder.ListAsync(document, line, column, diagnosticId, cancellationToken);
            var where = DisplayPath.Location(root, fullPath, line);
            var text = new ResponseBuilder(maxTokens);
            if (string.IsNullOrWhiteSpace(action))
            {
                RenderList(text, list, where);
                return text.ToString();
            }

            var item = CodeActionFinder.Select(list, action);
            var changes = await CodeActionEditor.ComputeAsync(snapshot, item.Action, document, cancellationToken);
            if (changes.Files.Count == 0)
            {
                text.Line($"'{item.Title}' at {where} {string.Join("; ", changes.NotApplied)}: nothing to show or apply.");
                return text.ToString();
            }

            if (!apply)
            {
                text.Line($"preview of {Describe(item)} at {where}: {changes.Files.Count} file(s), diff only — no file was changed");
                Notes(text, changes);
                Diff(text, changes, root);
                text.Line("to write it: call again with the same arguments and apply=true.");
                return text.ToString();
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
            CheckRenderer.Render(text, check, updated, root, CheckScope.Changed, offset: 0);
            text.Line("diff written:");
            Diff(text, changes, root);
            return text.ToString();
        });

    private static void RenderList(ResponseBuilder text, CodeActionList list, string where)
    {
        foreach (var diagnostic in list.Diagnostics)
        {
            text.Line($"{(diagnostic.Severity == DiagnosticSeverity.Error ? "error" : diagnostic.Severity.ToString().ToLowerInvariant())} {diagnostic.Id} at {where}: {diagnostic.Message}");
        }

        if (list.Actions.Count == 0)
        {
            text.Line($"no code actions at {where}.");
            return;
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
    }

    private static void Notes(ResponseBuilder text, CodeActionChanges changes)
    {
        if (changes.NotApplied.Count > 0)
        {
            text.Line($"note: the action also {string.Join("; ", changes.NotApplied)} (not shown, and not applied by Farol)");
        }
    }

    private static void Diff(ResponseBuilder text, CodeActionChanges changes, string root)
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
    }

    private static string Describe(CodeActionItem item) => $"'{item.Title}' ({Kind(item)})";

    private static string Kind(CodeActionItem item) =>
        item.DiagnosticId is null ? $"refactoring · {item.Provider}" : $"fix {item.DiagnosticId} · {item.Provider}";

    [LoggerMessage(Level = LogLevel.Information, Message = "Applied code action '{Action}' to {Files} file(s) for {User} via {Origin}")]
    private static partial void LogApplied(ILogger logger, string action, int files, string user, string origin);
}
