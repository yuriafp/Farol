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

/// <summary>One parameter of the new signature, as the agent passes it.</summary>
public sealed record ParameterArgument(
    [property: Description("The parameter's name: an existing one keeps its type and arguments; a new one needs 'type' and 'value'.")] string Name,
    [property: Description("For a new parameter: its type as the declaring file writes it, e.g. decimal (C#) or Decimal (VB).")] string? Type = null,
    [property: Description("For a new parameter: the expression every call site passes, written as is in C# and VB alike (e.g. 0).")] string? Value = null);

[McpServerToolType]
public sealed partial class ChangeSignatureTool(WorkspaceManager workspaces, ServerPermissions permissions, CallerContext caller, ILogger<ChangeSignatureTool> logger)
{
    [McpServerTool(Name = "dotnet_change_signature", Title = "Reorder, add or remove a C#/VB method's parameters", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ChangeOutput))]
    [Description(
        "Changes a method's or constructor's parameter list: reorder, remove, or add parameters with the value callers pass. Rewrites the " +
        "declaration, its overrides and implementations, and the arguments of every call site in C# and VB (named and omitted arguments " +
        "included). Returns a unified diff and changes no file; with apply=true it writes the files and returns a fresh dotnet_check.")]
    public Task<CallToolResult> Run(
        [Description(SymbolArgument.Description)] string symbol,
        [Description("The new parameter list in order: existing parameters by name ({\"name\":\"orderId\"}), new ones with a type and a value ({\"name\":\"discount\",\"type\":\"decimal\",\"value\":\"0\"}). A parameter left out is removed.")] ParameterArgument[] parameters,
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
                return ToolOutput.Of(ambiguity!, new ChangeOutput($"change signature of {symbol}", "nothing", [], [], [$"'{symbol}' names {candidates?.Count ?? 0} symbols: pass one of the candidates"], [], null, null));
            }

            var list = (parameters ?? []).Select(p => new SignatureParameter(p.Name, p.Type, p.Value)).ToList();
            var result = await ChangeSignatureRefactoring.ComputeAsync(snapshot, candidate.Symbol, list, workspaces.RootDirectory, cancellationToken);
            var key = $"dotnet_change_signature|{candidate.Id}|{string.Join(";", list.Select(p => $"{p.Name}:{p.Type}:{p.Value}"))}";
            var text = new ResponseBuilder(maxTokens);
            var output = await ChangeResponse.RespondAsync(ChangeRequest.For("dotnet_change_signature", key, result, apply), session, workspaces, text, cancellationToken);
            if (apply && output.Data.Mode == "applied")
            {
                LogApplied(logger, result.Title, result.Changes.Files.Count, caller.User, caller.Origin);
            }

            return output;
        });

    [LoggerMessage(Level = LogLevel.Information, Message = "Applied {Change} in {Files} file(s) for {User} via {Origin}")]
    private static partial void LogApplied(ILogger logger, string change, int files, string user, string origin);
}
