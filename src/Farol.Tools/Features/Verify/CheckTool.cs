using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using Farol.Core;
using Farol.Core.Paths;
using Farol.Core.Text;
using Farol.Engine.Diagnostics;
using Farol.Engine.Workspaces;
using Farol.Tools.Infrastructure;
using ModelContextProtocol.Server;

namespace Farol.Tools.Features.Verify;

[McpServerToolType]
public sealed class CheckTool(WorkspaceManager workspaces)
{
    private const string NothingToSay = "{}";

    private static readonly JsonSerializerOptions HookJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [McpServerTool(Name = "dotnet_check", Title = "Check C#/VB edits for new errors", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Compiler errors and warnings introduced since the workspace loaded — not the ones that were already there — in the edited files and in every " +
        "project their declaration changes can break, across C#, VB and every target framework. Call it after editing .cs/.vb files instead of " +
        "building: edits on disk are picked up automatically, and it takes about a second. Covers compiler diagnostics; analyzer rules (CA/IDE) show up in dotnet_build.")]
    public Task<string> Run(
        [Description("changed (default): files edited since load, plus the projects that depend on their declarations · file: one file ('path') · project: one project ('project') · solution: every project")] string? scope = null,
        [Description("Source file for scope=file, relative to the root as responses write paths.")] string? path = null,
        [Description("Project name for scope=project, as dotnet_overview lists it.")] string? project = null,
        [Description("Also list, separately, the diagnostics that already existed when the workspace loaded. Default false.")] bool includeExisting = false,
        [Description("text (default), or hook: the JSON a Claude Code PostToolUse hook returns — the new errors as a blocking reason, new warnings as added context, {} when the edits are clean or the workspace is still loading.")] string format = "text",
        [Description("For editor hooks: the file just edited, read before checking so the answer never lags the edit.")] string? edited = null,
        [McpHeader(ToolParameters.WorkspaceHeader), Description(ToolParameters.WorkspaceDescription)] string? workspace = null,
        [Description(ToolParameters.MaxTokensDescription)] int maxTokens = TokenBudget.DefaultTokens,
        [Description(ToolParameters.OffsetDescription)] int offset = 0,
        CancellationToken cancellationToken = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var hook = ParseFormat(format);
            var kind = ParseScope(scope, path, project);
            var session = workspaces.GetSession(workspace);
            var fullPath = kind == CheckScope.File && !string.IsNullOrWhiteSpace(path) ? workspaces.Paths.Resolve(path, session.Target.Directory) : null;
            if (fullPath is not null && IsSource(fullPath))
            {
                // Read before checking, like 'edited': a file created or changed a moment ago is never missed.
                session.NotifyEdited([fullPath]);
            }

            string? editedFile = null;
            if (!string.IsNullOrWhiteSpace(edited))
            {
                // A hook fires for every edit the agent makes; one outside the trusted directories or in a non-C#/VB file is not ours to check.
                editedFile = TryResolve(edited, session.Target.Directory);
                if (editedFile is null || !IsSource(editedFile))
                {
                    return hook ? NothingToSay : throw new FarolException(ErrorCodes.InvalidArgument, $"'{edited}' is not a C# or VB file Farol can read.", "Pass a .cs or .vb file inside the workspace.");
                }

                session.NotifyEdited([editedFile]);
            }

            WorkspaceSnapshot snapshot;
            try
            {
                // A hook must not hold the agent while a large solution loads: it reports nothing until the load is done.
                snapshot = await session.GetSnapshotAsync(wait: !hook, cancellationToken);
            }
            catch (FarolException ex) when (hook && ex.Code == ErrorCodes.WorkspaceNotReady)
            {
                return NothingToSay;
            }

            if (fullPath is not null)
            {
                SourceDocuments.Find(snapshot, fullPath, path!.Trim());
            }

            var result = await DiagnosticCheck.RunAsync(snapshot, new CheckRequest(kind, fullPath, project, includeExisting), cancellationToken);
            var text = new ResponseBuilder(maxTokens);
            CheckRenderer.Render(text, result, snapshot, workspaces.RootDirectory, kind, offset);
            return hook ? HookOutput(result, text.ToString(), editedFile is null ? null : DisplayPath.From(workspaces.RootDirectory, editedFile)) : text.ToString();
        });

    /// <summary>
    /// Claude Code reads a PostToolUse hook's output as JSON: "decision": "block" puts the reason in front of the agent,
    /// additionalContext adds a reminder, and anything else stays in the debug log.
    /// </summary>
    private static string HookOutput(CheckResult result, string text, string? edited)
    {
        if (result.New.Count == 0)
        {
            return NothingToSay;
        }

        var after = edited is null ? "since the workspace loaded" : $"after the edit to {edited}";
        if (result.New.Any(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error))
        {
            return JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["decision"] = "block",
                ["reason"] = $"Farol (dotnet_check) found new compiler errors {after}. Fix them before going on:\n{text}",
            }, HookJson);
        }

        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["hookSpecificOutput"] = new Dictionary<string, string>
            {
                ["hookEventName"] = "PostToolUse",
                ["additionalContext"] = $"Farol (dotnet_check) found new compiler warnings {after}:\n{text}",
            },
        }, HookJson);
    }

    private string? TryResolve(string path, string workspaceDirectory)
    {
        try
        {
            return workspaces.Paths.Resolve(path, workspaceDirectory);
        }
        catch (FarolException ex) when (ex.Code == ErrorCodes.PathNotTrusted)
        {
            return null;
        }
    }

    private static bool IsSource(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".vb", StringComparison.OrdinalIgnoreCase);

    private static bool ParseFormat(string? format) => (format ?? "text").Trim().ToUpperInvariant() switch
    {
        "TEXT" or "" => false,
        "HOOK" => true,
        _ => throw new FarolException(ErrorCodes.InvalidArgument, $"Unknown format '{format}'.", "Use text or hook."),
    };

    // Without an explicit scope, a path means one file and a project name means that project.
    private static CheckScope ParseScope(string? scope, string? path, string? project)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return !string.IsNullOrWhiteSpace(path) ? CheckScope.File
                : !string.IsNullOrWhiteSpace(project) ? CheckScope.Project
                : CheckScope.Changed;
        }

        return scope.Trim().ToUpperInvariant() switch
        {
            "CHANGED" => CheckScope.Changed,
            "FILE" => CheckScope.File,
            "PROJECT" => CheckScope.Project,
            "SOLUTION" => CheckScope.Solution,
            _ => throw new FarolException(ErrorCodes.InvalidArgument, $"Unknown scope '{scope}'.", "Use changed, file, project or solution."),
        };
    }
}
