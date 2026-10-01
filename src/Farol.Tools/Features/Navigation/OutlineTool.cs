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
        [Description("Source file path relative to the workspace root (e.g. src/App/Orders/OrderService.cs). Use this or 'symbol'.")] string? path = null,
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
            var snapshot = await workspaces.GetSession(workspace).GetSnapshotAsync(wait: true, cancellationToken);
            IReadOnlyList<OutlineEntry> entries;
            string title;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var fullPath = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
                var ids = snapshot.Solution.GetDocumentIdsWithFilePath(fullPath);
                if (ids.IsEmpty)
                {
                    throw new FarolException(ErrorCodes.InvalidArgument, $"'{path}' is not a source file of the workspace.", "Use a path relative to the workspace root.");
                }

                entries = await OutlineBuilder.ForDocumentAsync(snapshot.Solution.GetDocument(ids[0])!, cancellationToken);
                title = $"outline of {DisplayPath.From(root, fullPath)}";
            }
            else
            {
                var (candidate, ambiguity) = await SymbolArgument.ResolveAsync(snapshot, root, symbol!, cancellationToken);
                if (candidate is null)
                {
                    return ambiguity!;
                }

                if (candidate.Symbol is not INamedTypeSymbol type)
                {
                    throw new FarolException(ErrorCodes.InvalidArgument, $"'{symbol}' is a {SymbolFormatter.Kind(candidate.Symbol)}, not a type.", "Use dotnet_symbol for members.");
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
