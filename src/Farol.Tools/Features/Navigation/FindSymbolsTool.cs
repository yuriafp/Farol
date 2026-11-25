using System.ComponentModel;
using Farol.Core;
using Farol.Core.Text;
using Farol.Engine.Navigation;
using Farol.Engine.Workspaces;
using Farol.Tools.Infrastructure;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Farol.Tools.Features.Navigation;

[McpServerToolType]
public sealed class FindSymbolsTool(WorkspaceManager workspaces)
{
    [McpServerTool(Name = "dotnet_find_symbols", Title = "Find C#/VB declarations", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(FindSymbolsOutput))]
    [Description(
        "Finds where C#/VB types and members are declared, by name: exact, prefix, substring or camel-case humps ('OrdCalc' finds OrderCalculator). " +
        "Use instead of grep to locate a declaration: it ignores comments, strings and usages. Returns path:line and an id to pass to other dotnet_* tools.")]
    public Task<CallToolResult> Run(
        [Description("Name or pattern to search for.")] string query,
        [Description("any (default), type, class, interface, struct, enum, record, delegate, method, property, field or event.")] string kind = "any",
        [Description("Only declarations in this project (name as shown by dotnet_overview).")] string? project = null,
        [McpHeader(ToolParameters.WorkspaceHeader), Description(ToolParameters.WorkspaceDescription)] string? workspace = null,
        [Description(ToolParameters.MaxTokensDescription)] int maxTokens = TokenBudget.DefaultTokens,
        [Description(ToolParameters.OffsetDescription)] int offset = 0,
        CancellationToken cancellationToken = default) =>
        ToolGuard.RunAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                throw new FarolException(ErrorCodes.InvalidArgument, "'query' is empty.", "Pass a name or the start of one; camel-case humps work too ('OrdCalc' finds OrderCalculator).");
            }

            var session = workspaces.GetSession(workspace);
            if (IndexedAnswers.Answer(workspaces, session, indexed => FromIndex(indexed, query, kind, project, maxTokens, offset)) is { } answer)
            {
                return answer;
            }

            var snapshot = await session.GetSnapshotAsync(wait: true, cancellationToken);
            if (!string.IsNullOrWhiteSpace(project) && snapshot.Projects.FindByName(project).Count == 0)
            {
                var names = snapshot.Projects.Names;
                var more = names.Count > 40 ? $", … {names.Count - 40} more" : string.Empty;
                throw new FarolException(ErrorCodes.InvalidArgument, $"No project named '{project}'.", $"Projects: {string.Join(", ", names.Take(40))}{more}.");
            }

            var candidates = await DeclarationSearch.SearchAsync(snapshot, query, kind, project, cancellationToken);
            var text = new ResponseBuilder(maxTokens);
            if (candidates.Count == 0)
            {
                return ToolOutput.Of(
                    $"no declarations match '{query}' (kind: {kind}). Try a shorter pattern or kind='any'.",
                    new FindSymbolsOutput(query, kind, new Page<SymbolItem>(0, 0, null, [])));
            }

            var title = $"symbols matching '{query}'";
            text.List(title, candidates, c => NavigationText.Candidate(c, snapshot, workspaces.RootDirectory), offset, ResponseBuilder.OffsetHint);
            return ToolOutput.Of(text.ToString(), new FindSymbolsOutput(query, kind, Page.From(candidates, text.WindowOf(title), c => NavigationItems.Of(c, snapshot, workspaces.RootDirectory))));
        });

    private static ToolOutput<FindSymbolsOutput> FromIndex(IndexedAnswers indexed, string query, string kind, string? project, int maxTokens, int offset)
    {
        if (!DeclarationSearch.Kinds.Contains(kind.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            throw new FarolException(ErrorCodes.InvalidArgument, $"Unknown kind '{kind}'.", $"Use one of: {string.Join(", ", DeclarationSearch.Kinds)}.");
        }

        if (!string.IsNullOrWhiteSpace(project) && indexed.Index.Overview(indexed.Session.Target) is { } overview
            && !overview.Projects.Any(p => p.Name.Equals(project.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            var names = overview.Projects.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
            var more = names.Count > 40 ? $", … {names.Count - 40} more" : string.Empty;
            throw new FarolException(ErrorCodes.InvalidArgument, $"No project named '{project}'.", $"Projects: {string.Join(", ", names.Take(40))}{more}.");
        }

        var found = indexed.Index.FindSymbols(query, kind, project);
        var text = new ResponseBuilder(maxTokens);
        indexed.Header(text);
        if (found.Count == 0)
        {
            text.Line($"no declarations match '{query}' (kind: {kind}). Try a shorter pattern or kind='any'.");
            return ToolOutput.Of(text.ToString(), new FindSymbolsOutput(query, kind, new Page<SymbolItem>(0, 0, null, [])) { Index = indexed.Note });
        }

        var title = $"symbols matching '{query}'";
        text.List(title, found, indexed.Candidate, offset, ResponseBuilder.OffsetHint);
        return ToolOutput.Of(text.ToString(), new FindSymbolsOutput(query, kind, Page.From(found, text.WindowOf(title), indexed.Item)) { Index = indexed.Note });
    }
}
