using System.ComponentModel;
using System.Text;
using Farol.Core;
using Farol.Core.Paths;
using Farol.Core.Text;
using Farol.Engine.Building;
using Farol.Engine.CodeActions;
using Farol.Engine.Conversion;
using Farol.Engine.Packages;
using Farol.Engine.Workspaces;
using Farol.Tools.Infrastructure;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Farol.Tools.Features.Refactoring;

[McpServerToolType]
public sealed partial class ConvertProjectTool(WorkspaceManager workspaces, BuildRunner builds, ServerPermissions permissions, CallerContext caller, ILogger<ConvertProjectTool> logger)
{
    private const string Tool = "dotnet_convert_project";

    [McpServerTool(Name = Tool, Title = "Convert a project file to SDK-style or PackageReference", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(ConvertOutput))]
    [Description(
        "Changes a project file's format, not what it builds. kind=sdk_style turns a classic .csproj/.vbproj into an SDK-style one with the same " +
        ".NET Framework target, files, references and settings (packages.config becomes PackageReference with it); kind=package_reference moves " +
        "packages.config into PackageReference items and keeps the classic format. Returns the diff of the project file and the removed files, and " +
        "what it could not convert; changes no file. With apply=true it writes them, restores, reloads the workspace and compares the project's " +
        "files, references, packages and errors with before. Retargeting (net48 to net10.0) is not done here: see dotnet_migration_plan.")]
    public Task<CallToolResult> Run(
        [Description("The project, by name as dotnet_overview lists it.")] string project,
        [Description("sdk_style (classic project file to SDK-style, same target framework) or package_reference (packages.config to PackageReference).")] string kind,
        [Description("Write the change, restore, reload and compare. Refused when Farol runs with --read-only, or when a file changed since the diff was shown.")] bool apply = false,
        [McpHeader(ToolParameters.WorkspaceHeader), Description(ToolParameters.WorkspaceDescription)] string? workspace = null,
        [Description(ToolParameters.MaxTokensDescription)] int maxTokens = TokenBudget.DefaultTokens,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var conversionKind = (kind ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "sdk_style" or "sdk-style" or "sdk" => ConversionKind.SdkStyle,
                "package_reference" or "packagereference" or "package-reference" => ConversionKind.PackageReference,
                _ => throw new FarolException(ErrorCodes.InvalidArgument, $"'{kind}' is not a conversion.", "Pass kind=sdk_style or kind=package_reference."),
            };
            if (apply)
            {
                permissions.Demand("write files", "Call again without apply=true to get the change as a diff.");
            }

            var session = workspaces.GetSession(workspace);
            var snapshot = await session.GetSnapshotAsync(wait: true, cancellationToken);
            var matches = snapshot.Projects.FindByName((project ?? string.Empty).Trim());
            var entry = (matches.Count > 0 ? matches[0] : null)
                ?? throw new FarolException(ErrorCodes.InvalidArgument, $"No project named '{project}'.", $"Projects: {string.Join(", ", snapshot.Projects.Names.Take(40))}.");
            workspaces.Paths.Demand(entry.FilePath, $"Project '{entry.Name}'");

            var conversion = ProjectConverter.Convert(entry.FilePath, conversionKind);
            var encoding = EncodingOf(entry.FilePath);
            var files = new List<FileEdit> { new(entry.FilePath, conversion.OldText, conversion.NewText, encoding, entry.Name, entry.IsSdkStyle, ProjectFileUpdated: true) };
            files.AddRange(conversion.DeletedFiles.Select(path => new FileEdit(path, File.ReadAllText(path, EncodingOf(path)), null, EncodingOf(path), entry.Name, entry.IsSdkStyle, ProjectFileUpdated: true)));
            var changes = new CodeActionChanges(files, []);
            var title = $"convert {entry.Name} to {(conversionKind == ConversionKind.SdkStyle ? "an SDK-style project" : "PackageReference")}";
            var key = ChangeResponse.PreviewKey(session, $"{Tool}|{entry.FilePath}|{conversionKind}");
            var root = workspaces.RootDirectory;
            var text = new ResponseBuilder(maxTokens);
            var fileItems = ChangeResponse.Files(changes, [], root);

            if (!apply)
            {
                ChangeResponse.Remember(key, changes);
                text.Line($"preview of {title}: {files.Count} file(s), diff only — no file was changed");
                ChangeResponse.Notes(text, conversion.Notes, []);
                var preview = ChangeResponse.Diff(text, changes, root);
                text.Line("to write it: call again with the same arguments and apply=true; Farol then restores, reloads the workspace and compares the project with before.");
                return ToolOutput.Of(text.ToString(), new ConvertOutput(title, "preview", fileItems, conversion.Notes, preview, null));
            }

            ChangeResponse.EnsureAsPreviewed(key, changes, Tool, root);
            var reporter = new ToolProgress(progress);
            var context = NuGetContext.Load(session.Target.Directory);
            var before = await ConversionComparison.CaptureAsync(snapshot, context, entry.FilePath, cancellationToken);

            // The workspace reloads once, after the restore, rather than when the watcher sees the files change.
            session.ExpectProjectWrites(files.Select(f => (f.FilePath, f.NewText ?? string.Empty)));
            IReadOnlyList<string> written;
            bool restored;
            string? restoreOutput;
            try
            {
                written = await CodeActionEditor.ApplyAsync(files, workspaces.Paths, Tool, cancellationToken);
                LogApplied(logger, title, written.Count, caller.User, caller.Origin);
                (restored, restoreOutput) = await builds.RestoreAsync(session.Target, entry.FilePath, reporter, cancellationToken);
            }
            finally
            {
                // Whatever was written, or failed to be, the workspace reads the project files again.
                reporter.Report("reloading the workspace");
                _ = session.ReloadAsync();
            }

            await session.ReloadAsync().WaitAsync(cancellationToken);
            var updated = await session.GetSnapshotAsync(wait: true, cancellationToken);
            var after = await ConversionComparison.CaptureAsync(updated, context, entry.FilePath, cancellationToken);
            var delta = ConversionComparison.Compare(before, after);

            text.Line($"applied {title}: wrote {string.Join(", ", written.Select(p => DisplayPath.From(root, p)))}");
            ChangeResponse.Notes(text, conversion.Notes, []);
            if (!restored)
            {
                text.Line("restore failed, so packages may be missing from the reload:");
                foreach (var line in (restoreOutput ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    text.Line("  " + line.TrimEnd());
                }
            }

            var reload = Render(text, delta, restored);
            text.Line("diff written:");
            var diff = ChangeResponse.Diff(text, changes, root);
            return ToolOutput.Of(text.ToString(), new ConvertOutput(title, "applied", fileItems, conversion.Notes, diff, reload));
        });

    private static ReloadOutput Render(ResponseBuilder text, ConversionDelta delta, bool restored)
    {
        var same = delta.Same && restored;
        text.Line(same
            ? "after reloading: same source files, references and packages, and no new error"
            : !delta.Loaded
                ? "after reloading, the workspace has no source files for the project: the comparison proves nothing (see dotnet_workspace for load errors)"
                : "after reloading, the project differs from before:");
        void List(string label, IReadOnlyList<string> items)
        {
            if (items.Count > 0)
            {
                text.Line($"  {label} ({items.Count}): {string.Join(", ", items.Take(20))}{(items.Count > 20 ? ", …" : string.Empty)}");
            }
        }

        List("source files no longer compiled", delta.MissingDocuments);
        List("source files now compiled", delta.AddedDocuments);
        List("references lost", delta.MissingReferences);
        List("references the SDK adds", delta.AddedReferences);
        List("packages lost", delta.MissingPackages);
        List("packages now resolved", delta.AddedPackages);
        foreach (var error in delta.NewErrors.Take(20))
        {
            text.Line($"  new error: {error}");
        }

        text.More(delta.NewErrors.Count - Math.Min(20, delta.NewErrors.Count), "new errors");
        return new ReloadOutput(same, restored, delta.MissingDocuments, delta.AddedDocuments, delta.MissingReferences, delta.AddedReferences, delta.MissingPackages, delta.AddedPackages, delta.NewErrors);
    }

    private static UTF8Encoding EncodingOf(string path)
    {
        var bytes = new byte[3];
        using (var stream = File.OpenRead(path))
        {
            _ = stream.Read(bytes, 0, 3);
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: bytes is [0xEF, 0xBB, 0xBF]);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Applied {Change} in {Files} file(s) for {User} via {Origin}")]
    private static partial void LogApplied(ILogger logger, string change, int files, string user, string origin);
}

/// <summary>Structured output of dotnet_convert_project: the change, as a preview or applied, and how the reloaded project compares.</summary>
public sealed record ConvertOutput(
    string Change,
    [property: Description("preview (nothing written) or applied.")] string Mode,
    IReadOnlyList<ChangedFileItem> Files,
    IReadOnlyList<string> Notes,
    string? Diff,
    ReloadOutput? Reload);

/// <summary>The project after the restore and reload, compared with before the conversion.</summary>
public sealed record ReloadOutput(
    [property: Description("True when nothing was lost (files, references, packages) and no error is new.")] bool Same,
    bool Restored,
    IReadOnlyList<string> MissingDocuments,
    IReadOnlyList<string> AddedDocuments,
    IReadOnlyList<string> MissingReferences,
    IReadOnlyList<string> AddedReferences,
    IReadOnlyList<string> MissingPackages,
    IReadOnlyList<string> AddedPackages,
    IReadOnlyList<string> NewErrors);
