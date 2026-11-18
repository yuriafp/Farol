using System.ComponentModel;
using Farol.Core;
using Farol.Core.Paths;
using Farol.Core.Text;
using Farol.Engine.Refactoring;
using Farol.Engine.Workspaces;
using Farol.Tools.Infrastructure;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Farol.Tools.Features.Refactoring;

[McpServerToolType]
public sealed partial class ExtractMethodTool(WorkspaceManager workspaces, ServerPermissions permissions, CallerContext caller, ILogger<ExtractMethodTool> logger)
{
    [McpServerTool(Name = "dotnet_extract_method", Title = "Extract C#/VB statements into a new method", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ChangeOutput))]
    [Description(
        "Moves a range of whole statements into a new method with the parameters and return value they need, and calls it in their place " +
        "(Roslyn's extract method, as in Visual Studio), in C# and VB. Returns a unified diff and changes no file; with apply=true it writes " +
        "the file and returns a fresh dotnet_check.")]
    public Task<CallToolResult> Run(
        [Description("Source file, relative to the root as responses write paths (e.g. src/App/Orders/OrderService.cs).")] string path,
        [Description("1-based first line of the statements.")] int startLine,
        [Description("1-based last line of the statements.")] int endLine,
        [Description("The new method's name, e.g. LoadOrder.")] string name,
        [Description("Write the change and return a fresh check. Refused when Farol runs with --read-only, or when the file changed since the diff was shown.")] bool apply = false,
        [McpHeader(ToolParameters.WorkspaceHeader), Description(ToolParameters.WorkspaceDescription)] string? workspace = null,
        [Description(ToolParameters.MaxTokensDescription)] int maxTokens = TokenBudget.DefaultTokens,
        CancellationToken cancellationToken = default) =>
        ToolGuard.RunAsync(async () =>
        {
            if (apply)
            {
                permissions.Demand("write files", "Call again without apply=true to get the change as a diff.");
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                throw new FarolException(ErrorCodes.InvalidArgument, "'path' is empty.", "Pass the source file, relative to the root as responses write paths.");
            }

            var session = workspaces.GetSession(workspace, path);
            var fullPath = workspaces.Paths.Resolve(path, session.Target.Directory);
            var snapshot = await session.GetSnapshotAsync(wait: true, cancellationToken);
            var document = snapshot.Solution.GetDocument(SourceDocuments.Find(snapshot, fullPath, path.Trim())[0])!;
            var result = await ExtractMethodRefactoring.ComputeAsync(snapshot, document, startLine, endLine, name, DisplayPath.From(workspaces.RootDirectory, fullPath), cancellationToken);
            var key = $"dotnet_extract_method|{fullPath}|{startLine}|{endLine}|{name?.Trim()}";
            var text = new ResponseBuilder(maxTokens);
            var output = await ChangeResponse.RespondAsync(ChangeRequest.For("dotnet_extract_method", key, result, apply), session, workspaces, text, cancellationToken);
            if (apply && output.Data.Mode == "applied")
            {
                LogApplied(logger, result.Title, result.Changes.Files.Count, caller.User, caller.Origin);
            }

            return output;
        });

    [LoggerMessage(Level = LogLevel.Information, Message = "Applied {Change} in {Files} file(s) for {User} via {Origin}")]
    private static partial void LogApplied(ILogger logger, string change, int files, string user, string origin);
}
