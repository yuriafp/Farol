using System.ComponentModel;
using Farol.Core;
using Farol.Core.Text;
using Farol.Engine.Navigation;
using Farol.Engine.Refactoring;
using Farol.Engine.Workspaces;
using Farol.Tools.Features.Navigation;
using Farol.Tools.Infrastructure;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Farol.Tools.Features.Refactoring;

[McpServerToolType]
public sealed partial class MoveTypeTool(WorkspaceManager workspaces, ServerPermissions permissions, CallerContext caller, ILogger<MoveTypeTool> logger)
{
    [McpServerTool(Name = "dotnet_move_type", Title = "Move a C#/VB type to another namespace or file", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ChangeOutput))]
    [Description(
        "Moves a top-level type to another namespace and/or file of its project, and fixes its uses: the using/Imports they need are added " +
        "and names written with the old namespace are rewritten, in C# and VB. New, moved or emptied files update the project file of " +
        "classic projects. Returns a unified diff and changes no file; with apply=true it writes the files and returns a fresh dotnet_check.")]
    public Task<CallToolResult> Run(
        [Description(SymbolArgument.Description)] string symbol,
        [Description("The namespace to move the type to, e.g. Legacy.Core.Billing. Omit to keep it.")] string? toNamespace = null,
        [Description("The file to move the type to, relative to the root as responses write paths: a new file, or a source file of the same project. Omit to keep it in its file.")] string? toFile = null,
        [Description("Write the change and return a fresh check. Refused when Farol runs with --read-only, or when a file changed since the diff was shown.")] bool apply = false,
        [McpHeader(ToolParameters.WorkspaceHeader), Description(ToolParameters.WorkspaceDescription)] string? workspace = null,
        [Description(ToolParameters.MaxTokensDescription)] int maxTokens = TokenBudget.DefaultTokens,
        CancellationToken cancellationToken = default) =>
        ToolGuard.RunAsync(async () =>
        {
            if (apply)
            {
                permissions.Demand("write files", "Call again without apply=true to get the change as a diff.");
            }

            if (string.IsNullOrWhiteSpace(toNamespace) && string.IsNullOrWhiteSpace(toFile))
            {
                throw new FarolException(ErrorCodes.InvalidArgument, "Nothing to move to: pass toNamespace, toFile, or both.", "e.g. toNamespace=Legacy.Core.Billing.");
            }

            var session = workspaces.GetSession(workspace, SymbolLocator.LocationPath(symbol));
            var snapshot = await session.GetSnapshotAsync(wait: true, cancellationToken);
            var (candidate, ambiguity, candidates) = await SymbolArgument.ResolveAsync(snapshot, workspaces.Paths, symbol, cancellationToken);
            if (candidate is null)
            {
                return ToolOutput.Of(ambiguity!, new ChangeOutput($"move {symbol}", "nothing", [], [], [$"'{symbol}' names {candidates?.Count ?? 0} symbols: pass one of the candidates"], [], null, null));
            }

            var file = string.IsNullOrWhiteSpace(toFile) ? null : workspaces.Paths.Resolve(toFile.Trim(), session.Target.Directory);
            if (file is not null)
            {
                workspaces.Paths.Demand(file, $"File '{toFile!.Trim()}'");
            }

            var result = await MoveTypeRefactoring.ComputeAsync(snapshot, candidate.Symbol, file, toNamespace, workspaces.RootDirectory, cancellationToken);
            var key = $"dotnet_move_type|{candidate.Id}|{toNamespace?.Trim()}|{file}";
            var text = new ResponseBuilder(maxTokens);
            var output = await ChangeResponse.RespondAsync(ChangeRequest.For("dotnet_move_type", key, result, apply), session, workspaces, text, cancellationToken);
            if (apply && output.Data.Mode == "applied")
            {
                LogApplied(logger, result.Title, result.Changes.Files.Count, caller.User, caller.Origin);
            }

            return output;
        });

    [LoggerMessage(Level = LogLevel.Information, Message = "Applied {Change} in {Files} file(s) for {User} via {Origin}")]
    private static partial void LogApplied(ILogger logger, string change, int files, string user, string origin);
}
