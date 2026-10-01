using System.ComponentModel;
using Farol.Core;
using Farol.Core.Paths;
using Farol.Core.Text;
using Farol.Engine.Navigation;
using Farol.Engine.Workspaces;
using Farol.Tools.Infrastructure;
using ModelContextProtocol.Server;

namespace Farol.Tools.Features.Navigation;

[McpServerToolType]
public sealed class FindReferencesTool(WorkspaceManager workspaces)
{
    [McpServerTool(Name = "dotnet_find_references", Title = "Find references to a C#/VB symbol", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Compiler-accurate references to one C#/VB symbol across the solution and every target framework, grouped by project, each classified " +
        "as call, new, read, write, method group or reference — plus 'markup' references the compiler never sees: WebForms (.aspx/.ascx/.master " +
        "event handlers, controls, <% %> expressions), .asmx/.svc/.ashx/.asax directives and XAML (x:Class, x:Name, event handlers, converters). " +
        "Use instead of grep before changing or removing a symbol.")]
    public Task<string> Run(
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
            var snapshot = await workspaces.GetSession(workspace).GetSnapshotAsync(wait: true, cancellationToken);
            var (candidate, ambiguity) = await SymbolArgument.ResolveAsync(snapshot, root, symbol, cancellationToken);
            if (candidate is null)
            {
                return ambiguity!;
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

            if (references.Count == 0)
            {
                text.Line("no references found in code or markup (it may still be used through reflection, configuration files or other repositories).");
                return text.ToString();
            }

            NavigationText.Hits(text, references, root, offset);
            return text.ToString();
        });

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
