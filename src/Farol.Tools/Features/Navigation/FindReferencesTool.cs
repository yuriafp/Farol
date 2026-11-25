using System.ComponentModel;
using Farol.Core;
using Farol.Core.Paths;
using Farol.Core.Text;
using Farol.Engine.Indexing;
using Farol.Engine.Navigation;
using Farol.Engine.Workspaces;
using Farol.Tools.Infrastructure;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Farol.Tools.Features.Navigation;

[McpServerToolType]
public sealed class FindReferencesTool(WorkspaceManager workspaces)
{
    [McpServerTool(Name = "dotnet_find_references", Title = "Find references to a C#/VB symbol", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(FindReferencesOutput))]
    [Description(
        "Compiler-accurate references to one C#/VB symbol across the solution and every target framework, grouped by project, each classified " +
        "as call, new, read, write, method group or reference — plus 'markup' references the compiler never sees: WebForms (.aspx/.ascx/.master " +
        "event handlers, controls, <% %> expressions), .asmx/.svc/.ashx/.asax directives and XAML (x:Class, x:Name, event handlers, converters). " +
        "A member's uses include those through the interface members it implements or the members it overrides, marked 'via' that type. " +
        "Use instead of grep before changing or removing a symbol.")]
    public Task<CallToolResult> Run(
        [Description(SymbolArgument.Description)] string symbol,
        [Description("Only these kinds, comma-separated: call, new, read, write, method group, reference, markup. Default: all.")] string? kinds = null,
        [McpHeader(ToolParameters.WorkspaceHeader), Description(ToolParameters.WorkspaceDescription)] string? workspace = null,
        [Description(ToolParameters.MaxTokensDescription)] int maxTokens = TokenBudget.DefaultTokens,
        [Description(ToolParameters.OffsetDescription)] int offset = 0,
        CancellationToken cancellationToken = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var filter = ParseKinds(kinds);
            var root = workspaces.RootDirectory;
            var session = workspaces.GetSession(workspace, SymbolLocator.LocationPath(symbol));
            if (IndexedAnswers.Answer(workspaces, session, indexed => FromIndex(indexed, workspaces.Paths, symbol, filter, maxTokens, offset)) is { } answer)
            {
                return answer;
            }

            var snapshot = await session.GetSnapshotAsync(wait: true, cancellationToken);
            var (candidate, ambiguity, candidates) = await SymbolArgument.ResolveAsync(snapshot, workspaces.Paths, symbol, cancellationToken);
            if (candidate is null)
            {
                return ToolOutput.Of(ambiguity!, new FindReferencesOutput(null, 0, 0, [], [], null, candidates));
            }

            var result = await ReferenceFinder.FindAsync(snapshot, candidate, cancellationToken);
            var references = result.References.Where(r => filter is null || filter.Contains(r.Kind)).ToList();
            var projects = references.Select(r => r.Project).Distinct(StringComparer.Ordinal).Count();

            var text = new ResponseBuilder(maxTokens);
            text.Line($"references to {SymbolFormatter.Display(candidate.Symbol)}: {references.Count} in {projects} project(s)");
            if (result.Definitions.Count > 0)
            {
                text.Line($"defined at: {string.Join(", ", result.Definitions.Select(d => DisplayPath.Location(root, d.FilePath, d.Line)))}");
            }

            var through = new List<ThroughItem>();
            foreach (var member in result.Through)
            {
                var via = ReferenceFinder.Via(member);
                if (references.Count(r => r.Via == via) is > 0 and var count)
                {
                    through.Add(new ThroughItem(NavigationItems.Of(member, root), via, count));
                    text.Line($"{count} of them through {SymbolFormatter.Display(member)} · {SymbolFormatter.Location(member, root)}, marked 'via {via}'");
                }
            }

            if (references.Count == 0)
            {
                text.Line("no references found in code or markup (it may still be used through reflection, configuration files or other repositories).");
                return ToolOutput.Of(text.ToString(), Output(new Page<HitItem>(0, 0, null, [])));
            }

            var window = NavigationText.Hits(text, references, root, offset);
            return ToolOutput.Of(text.ToString(), Output(Page.From(references, window, h => NavigationItems.Of(h, root))));

            FindReferencesOutput Output(Page<HitItem> page) => new(
                NavigationItems.Of(candidate, snapshot, root),
                references.Count,
                projects,
                [.. result.Definitions.Select(d => DisplayPath.Location(root, d.FilePath, d.Line))],
                through,
                page,
                null);
        });

    private static ToolOutput<FindReferencesOutput>? FromIndex(IndexedAnswers indexed, PathSandbox paths, string symbol, HashSet<string>? filter, int maxTokens, int offset)
    {
        if (indexed.Resolve(paths, symbol) is not var (candidate, ambiguity, candidates))
        {
            return null;
        }

        if (candidate is null)
        {
            return ToolOutput.Of(ambiguity!, new FindReferencesOutput(null, 0, 0, [], [], null, candidates) { Index = indexed.Note });
        }

        var root = indexed.Root;
        var result = indexed.Index.References(candidate);
        var references = result.References.Where(r => filter is null || filter.Contains(r.Kind)).Select(IndexedAnswers.Hit).ToList();
        var projects = references.Select(r => r.Project).Distinct(StringComparer.Ordinal).Count();
        var definedAt = result.Definitions.Select(d => DisplayPath.Location(root, d.FilePath, d.Line)).ToList();

        var text = new ResponseBuilder(maxTokens);
        indexed.Header(text);
        text.Line($"references to {candidate.Display}: {references.Count} in {projects} project(s)");
        if (definedAt.Count > 0)
        {
            text.Line($"defined at: {string.Join(", ", definedAt)}");
        }

        var through = new List<ThroughItem>();
        foreach (var member in result.Through)
        {
            var via = member.Via ?? string.Empty;
            if (references.Count(r => r.Via == via) is > 0 and var count)
            {
                through.Add(new ThroughItem(indexed.Item(member), via, count));
                text.Line($"{count} of them through {member.Display} · {indexed.Location(member)}, marked 'via {via}'");
            }
        }

        if (references.Count == 0)
        {
            text.Line("no references found in code or markup (it may still be used through reflection, configuration files or other repositories).");
            return ToolOutput.Of(text.ToString(), Output(new Page<HitItem>(0, 0, null, [])));
        }

        var window = NavigationText.Hits(text, references, root, offset, indexed.Mark);
        return ToolOutput.Of(text.ToString(), Output(Page.From(references, window, h => NavigationItems.Of(h, root))));

        FindReferencesOutput Output(Page<HitItem> page) =>
            new(indexed.Item(candidate), references.Count, projects, definedAt, through, page, null) { Index = indexed.Note };
    }

    private static HashSet<string>? ParseKinds(string? kinds)
    {
        if (string.IsNullOrWhiteSpace(kinds))
        {
            return null;
        }

        var parsed = kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (parsed.FirstOrDefault(k => !ReferenceFinder.Kinds.Contains(k, StringComparer.OrdinalIgnoreCase)) is { } unknown)
        {
            throw new FarolException(ErrorCodes.InvalidArgument, $"Unknown reference kind '{unknown}'.", $"Use: {string.Join(", ", ReferenceFinder.Kinds)}.");
        }

        return parsed;
    }
}
