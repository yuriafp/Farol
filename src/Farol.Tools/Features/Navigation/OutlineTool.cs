using System.ComponentModel;
using Farol.Core;
using Farol.Core.Paths;
using Farol.Core.Text;
using Farol.Engine.Navigation;
using Farol.Engine.Workspaces;
using Farol.Tools.Infrastructure;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace Farol.Tools.Features.Navigation;

[McpServerToolType]
public sealed class OutlineTool(WorkspaceManager workspaces)
{
    [McpServerTool(Name = "dotnet_outline", Title = "Outline a C#/VB file or type", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "The shape of a C#/VB source file or type: types and members with signatures and line numbers, no bodies. " +
        "Use before reading a large file to find the part you need; a fraction of the tokens of the full text.")]
    public Task<string> Run(
        [Description("Source file, relative to the root as responses write paths (e.g. src/App/Orders/OrderService.cs). Use this or 'symbol'.")] string? path = null,
        [Description("A type to outline instead of a file (name, dotted name or id); lists members from every partial declaration.")] string? symbol = null,
        [McpHeader(ToolParameters.WorkspaceHeader), Description(ToolParameters.WorkspaceDescription)] string? workspace = null,
        [Description(ToolParameters.MaxTokensDescription)] int maxTokens = TokenBudget.DefaultTokens,
        CancellationToken cancellationToken = default) =>
        ToolGuard.RunAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(path) == string.IsNullOrWhiteSpace(symbol))
            {
                throw new FarolException(ErrorCodes.InvalidArgument, "Pass exactly one of 'path' or 'symbol'.");
            }

            var root = workspaces.RootDirectory;
            var session = workspaces.GetSession(workspace);
            var fullPath = string.IsNullOrWhiteSpace(path) ? null : workspaces.Paths.Resolve(path, session.Target.Directory);
            var snapshot = await session.GetSnapshotAsync(wait: true, cancellationToken);
            IReadOnlyList<OutlineEntry> entries;
            string title;
            if (fullPath is not null)
            {
                var ids = SourceDocuments.Find(snapshot, fullPath, path!.Trim());
                entries = await OutlineBuilder.ForDocumentAsync(snapshot.Solution.GetDocument(ids[0])!, cancellationToken);
                title = $"outline of {DisplayPath.From(root, fullPath)}";
            }
            else
            {
                var (candidate, ambiguity) = await SymbolArgument.ResolveAsync(snapshot, workspaces.Paths, symbol!, cancellationToken);
                if (candidate is null)
                {
                    return ambiguity!;
                }

                if (candidate.Symbol is not INamedTypeSymbol type)
                {
                    throw new FarolException(ErrorCodes.InvalidArgument, $"'{symbol}' is a {SymbolFormatter.Kind(candidate.Symbol)}, not a type.", "Use dotnet_symbol for members.");
                }

                if (!type.Locations.Any(l => l.IsInSource))
                {
                    throw new FarolException(
                        ErrorCodes.InvalidArgument,
                        $"'{symbol}' is {SymbolFormatter.Display(type)} from {type.ContainingAssembly?.Name ?? "a referenced assembly"}, which has no source in the workspace.",
                        "dotnet_symbol describes it; for a type from a NuGet package, dotnet_package_api lists its members.");
                }

                entries = OutlineBuilder.ForType(type);
                title = $"outline of {SymbolFormatter.Display(type)}";
            }

            var text = new ResponseBuilder(maxTokens);
            text.Line($"{title} ({entries.Count} declarations)");
            var multipleFiles = entries.Select(e => e.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
            var written = 0;
            foreach (var entry in entries)
            {
                var where = multipleFiles ? DisplayPath.Location(root, entry.FilePath, entry.Line) : entry.Line.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!text.TryLine($"{new string(' ', entry.Depth * 2)}- {where} {SymbolFormatter.Kind(entry.Symbol)} {entry.Symbol.ToDisplayString(SymbolFormatter.Member)}"))
                {
                    break;
                }

                written++;
            }

            text.More(entries.Count - written, "raise maxTokens, or outline a single type with 'symbol'");
            return text.ToString();
        });
}
