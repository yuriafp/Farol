using System.ComponentModel;
using Farol.Core;
using Farol.Core.Paths;
using Farol.Core.Text;
using Farol.Engine.Indexing;
using Farol.Engine.Navigation;
using Farol.Engine.Workspaces;
using Farol.Tools.Infrastructure;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Farol.Tools.Features.Navigation;

[McpServerToolType]
public sealed class OutlineTool(WorkspaceManager workspaces)
{
    [McpServerTool(Name = "dotnet_outline", Title = "Outline a C#/VB file or type", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(OutlineOutput))]
    [Description(
        "The shape of a C#/VB source file or type: types and members with signatures and line numbers, no bodies. " +
        "Use before reading a large file to find the part you need; a fraction of the tokens of the full text.")]
    public Task<CallToolResult> Run(
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
            var session = workspaces.GetSession(workspace, string.IsNullOrWhiteSpace(path) ? SymbolLocator.LocationPath(symbol) : path);
            var fullPath = string.IsNullOrWhiteSpace(path) ? null : workspaces.Paths.Resolve(path, session.Target.Directory);
            if (IndexedAnswers.Answer(workspaces, session, indexed => FromIndex(indexed, workspaces.Paths, fullPath, symbol, maxTokens)) is { } answer)
            {
                return answer;
            }

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
                var (candidate, ambiguity, candidates) = await SymbolArgument.ResolveAsync(snapshot, workspaces.Paths, symbol!, cancellationToken);
                if (candidate is null)
                {
                    return ToolOutput.Of(ambiguity!, new OutlineOutput(symbol!, 0, null, candidates));
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
            var written = new List<OutlineItem>();
            foreach (var entry in entries)
            {
                var where = multipleFiles ? DisplayPath.Location(root, entry.FilePath, entry.Line) : entry.Line.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var kind = SymbolFormatter.Kind(entry.Symbol);
                var signature = entry.Symbol.ToDisplayString(SymbolFormatter.Member);
                if (!text.TryLine($"{new string(' ', entry.Depth * 2)}- {where} {kind} {signature}"))
                {
                    break;
                }

                written.Add(new OutlineItem(entry.Depth, kind, signature, DisplayPath.Location(root, entry.FilePath, entry.Line)));
            }

            text.More(entries.Count - written.Count, "raise maxTokens, or outline a single type with 'symbol'");
            return ToolOutput.Of(text.ToString(), new OutlineOutput(title, entries.Count, written, null));
        });

    private static ToolOutput<OutlineOutput>? FromIndex(IndexedAnswers indexed, PathSandbox paths, string? fullPath, string? symbol, int maxTokens)
    {
        var root = indexed.Root;
        IReadOnlyList<IndexedDeclaration> entries;
        string title;
        if (fullPath is not null)
        {
            if (!indexed.Index.Knows(fullPath))
            {
                return null;
            }

            entries = indexed.Index.OutlineOfFile(fullPath);
            title = $"outline of {DisplayPath.From(root, fullPath)}";
        }
        else
        {
            if (indexed.Resolve(paths, symbol!) is not var (candidate, ambiguity, candidates))
            {
                return null;
            }

            if (candidate is null)
            {
                return ToolOutput.Of(ambiguity!, new OutlineOutput(symbol!, 0, null, candidates) { Index = indexed.Note });
            }

            if (!candidate.Match.StartsWith("type", StringComparison.Ordinal))
            {
                throw new FarolException(ErrorCodes.InvalidArgument, $"'{symbol}' is a {candidate.Kind}, not a type.", "Use dotnet_symbol for members.");
            }

            entries = indexed.Index.OutlineOfType(candidate);
            title = $"outline of {candidate.Display}";
        }

        var text = new ResponseBuilder(maxTokens);
        indexed.Header(text);
        var multipleFiles = entries.Select(e => e.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
        text.Line($"{title} ({entries.Count} declarations){(multipleFiles ? string.Empty : indexed.Mark(entries.Count > 0 ? entries[0].FilePath : fullPath))}");
        var written = new List<OutlineItem>();
        foreach (var entry in entries)
        {
            var where = multipleFiles ? DisplayPath.Location(root, entry.FilePath, entry.Line) : entry.Line.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var mark = multipleFiles ? indexed.Mark(entry.FilePath) : string.Empty;
            if (!text.TryLine($"{new string(' ', entry.Depth * 2)}- {where} {entry.Symbol.Kind} {entry.Symbol.Member}{mark}"))
            {
                break;
            }

            written.Add(new OutlineItem(entry.Depth, entry.Symbol.Kind, entry.Symbol.Member, DisplayPath.Location(root, entry.FilePath, entry.Line)));
        }

        text.More(entries.Count - written.Count, "raise maxTokens, or outline a single type with 'symbol'");
        return ToolOutput.Of(text.ToString(), new OutlineOutput(title, entries.Count, written, null) { Index = indexed.Note });
    }
}
