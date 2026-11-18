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
public sealed partial class RenameTool(WorkspaceManager workspaces, ServerPermissions permissions, CallerContext caller, ILogger<RenameTool> logger)
{
    [McpServerTool(Name = "dotnet_rename", Title = "Rename a C#/VB symbol everywhere", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ChangeOutput))]
    [Description(
        "Renames a type, method, property, field, event, parameter or local in every C# and VB file and in WebForms and XAML markup, " +
        "and renames the file named after a type (with its XAML or .resx and the project file items). Refuses a name that clashes with " +
        "an existing declaration or would make a reference mean something else. Returns a unified diff and changes no file; with " +
        "apply=true it writes the files and returns a fresh dotnet_check. Use it instead of search-and-replace.")]
    public Task<CallToolResult> Run(
        [Description(SymbolArgument.Description)] string symbol,
        [Description("The new name, e.g. CalculateTotal.")] string newName,
        [Description("Also rename the file named after a type (Old.cs → New.cs, Old.xaml.cs and Old.xaml, Old.Designer.cs). Default true.")] bool renameFile = true,
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

            var session = workspaces.GetSession(workspace, SymbolLocator.LocationPath(symbol));
            var snapshot = await session.GetSnapshotAsync(wait: true, cancellationToken);
            var (candidate, ambiguity, candidates) = await SymbolArgument.ResolveAsync(snapshot, workspaces.Paths, symbol, cancellationToken);
            if (candidate is null)
            {
                return ToolOutput.Of(ambiguity!, new ChangeOutput($"rename {symbol}", "nothing", [], [], [$"'{symbol}' names {candidates?.Count ?? 0} symbols: pass one of the candidates"], [], null, null));
            }

            var rename = await RenameRefactoring.ComputeAsync(snapshot, candidate, newName, renameFile, workspaces.RootDirectory, cancellationToken);
            var request = new ChangeRequest(
                "dotnet_rename",
                $"rename {rename.Symbol} → {rename.NewName}",
                $"dotnet_rename|{candidate.Id}|{rename.NewName}|{renameFile}",
                rename.Changes,
                rename.Moves,
                rename.Notes,
                apply)
            {
                Renamed = (rename.OldName, rename.NewName),
            };
            var text = new ResponseBuilder(maxTokens);
            var output = await ChangeResponse.RespondAsync(request, session, workspaces, text, cancellationToken);
            if (apply && output.Data.Mode == "applied")
            {
                LogApplied(logger, rename.Symbol, rename.NewName, rename.Changes.Files.Count, caller.User, caller.Origin);
            }

            return output;
        });

    [LoggerMessage(Level = LogLevel.Information, Message = "Renamed {Symbol} to {NewName} in {Files} file(s) for {User} via {Origin}")]
    private static partial void LogApplied(ILogger logger, string symbol, string newName, int files, string user, string origin);
}
