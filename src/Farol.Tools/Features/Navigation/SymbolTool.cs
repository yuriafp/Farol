using System.ComponentModel;
using Farol.Core;
using Farol.Core.Text;
using Farol.Engine.Navigation;
using Farol.Engine.Packages;
using Farol.Engine.Workspaces;
using Farol.Tools.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;

namespace Farol.Tools.Features.Navigation;

[McpServerToolType]
public sealed class SymbolTool(WorkspaceManager workspaces)
{
    private static readonly string[] IncludeOptions = ["signature", "docs", "source"];

    [McpServerTool(Name = "dotnet_symbol", Title = "Inspect one C#/VB symbol", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Signature, location, documentation and optionally the source of one C#/VB symbol, without reading its whole file. " +
        "External symbols (packages, the framework) come with the package and version they are from; their source is decompiled. " +
        "For a type, include='source' returns a skeleton (members with signatures, no bodies). An ambiguous name returns the candidates with their ids.")]
    public Task<string> Run(
        [Description(SymbolArgument.Description)] string symbol,
        [Description("Comma-separated: signature (always), docs (XML documentation summary), source (the declaration's code; a skeleton for types). Default: signature,docs.")] string include = "signature,docs",
        [McpHeader(ToolParameters.WorkspaceHeader), Description(ToolParameters.WorkspaceDescription)] string? workspace = null,
        [Description(ToolParameters.MaxTokensDescription)] int maxTokens = TokenBudget.DefaultTokens,
        CancellationToken cancellationToken = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var parts = include.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.FirstOrDefault(p => !IncludeOptions.Contains(p, StringComparer.OrdinalIgnoreCase)) is { } unknown)
            {
                throw new FarolException(ErrorCodes.InvalidArgument, $"Unknown include option '{unknown}'.", "Use signature, docs and/or source.");
            }

            var root = workspaces.RootDirectory;
            var snapshot = await workspaces.GetSession(workspace).GetSnapshotAsync(wait: true, cancellationToken);
            var (candidate, ambiguity) = await SymbolArgument.ResolveAsync(snapshot, workspaces.Paths, symbol, cancellationToken);
            if (candidate is null)
            {
                return ambiguity!;
            }

            var target = candidate.Symbol;
            var text = new ResponseBuilder(maxTokens);
            text.Line(SymbolFormatter.Display(target));
            var project = snapshot.Projects.Get(candidate.Variants[0].ProjectId);
            var frameworks = candidate.Variants.Select(v => snapshot.Projects.Get(v.ProjectId)?.TargetFramework).OfType<string>().Distinct().Order(StringComparer.OrdinalIgnoreCase);
            var assembly = IsExternal(target) ? await ExternalAssemblyAsync(snapshot.Solution, target, cancellationToken) : null;
            text.Line(project is null && IsExternal(target)
                ? $"namespace: {SymbolFormatter.Namespace(target)} · assembly: {target.ContainingAssembly?.Identity.GetDisplayName() ?? "unknown"}"
                : $"namespace: {SymbolFormatter.Namespace(target)} · project: {project?.Name ?? target.ContainingAssembly?.Name} ({string.Join(", ", frameworks)})");
            text.Line($"id: {candidate.Id}");
            text.Line(assembly is null
                ? $"declared at: {string.Join(", ", target.Locations.Where(l => l.IsInSource).Select(l => Core.Paths.DisplayPath.Location(root, l.SourceTree!.FilePath, l.GetLineSpan().StartLinePosition.Line + 1)).DefaultIfEmpty(SymbolFormatter.Location(target, root)))}"
                : $"declared in: {MetadataSource.Describe(assembly)}");

            if (target is INamedTypeSymbol type)
            {
                var bases = new[] { type.BaseType }.OfType<INamedTypeSymbol>().Where(b => b.SpecialType != SpecialType.System_Object).Concat(type.Interfaces).ToList();
                if (bases.Count > 0)
                {
                    text.Line($"inherits: {string.Join(", ", bases.Select(b => b.ToDisplayString(SymbolFormatter.Signature)))}");
                }
            }

            if (parts.Contains("docs", StringComparer.OrdinalIgnoreCase))
            {
                text.Line($"summary: {SymbolFormatter.Summary(target, cancellationToken) ?? "(no XML documentation)"}");
            }

            if (parts.Contains("source", StringComparer.OrdinalIgnoreCase))
            {
                if (IsExternal(target))
                {
                    AppendExternalSource(text, target, assembly);
                }
                else
                {
                    await AppendSourceAsync(text, snapshot.Solution, target, root, cancellationToken);
                }
            }

            return text.ToString();
        });

    private static bool IsExternal(ISymbol symbol) => !symbol.Locations.Any(l => l.IsInSource);

    /// <summary>
    /// The file an external symbol's assembly was read from. Assembly symbols are per compilation, so match by identity,
    /// preferring compilations that are already built.
    /// </summary>
    private static async Task<string?> ExternalAssemblyAsync(Solution solution, ISymbol symbol, CancellationToken cancellationToken)
    {
        if (symbol.ContainingAssembly is not { } assembly)
        {
            return null;
        }

        var built = solution.Projects.Select(p => p.TryGetCompilation(out var c) ? c : null).OfType<Compilation>().ToList();
        foreach (var compilation in built)
        {
            if (Find(compilation) is { } path)
            {
                return path;
            }
        }

        foreach (var project in solution.Projects.Where(p => !p.TryGetCompilation(out _)))
        {
            if (await project.GetCompilationAsync(cancellationToken) is { } compilation && Find(compilation) is { } path)
            {
                return path;
            }
        }

        return null;

        string? Find(Compilation compilation) =>
            MetadataSource.AssemblyPath(compilation, symbol)
            ?? compilation.References.OfType<PortableExecutableReference>()
                .FirstOrDefault(r => r.FilePath is not null && compilation.GetAssemblyOrModuleSymbol(r) is IAssemblySymbol a && a.Identity.Equals(assembly.Identity))?.FilePath;
    }

    /// <summary>External types get their public API as a skeleton; external members are decompiled from the implementation assembly.</summary>
    private static void AppendExternalSource(ResponseBuilder text, ISymbol symbol, string? assembly)
    {
        if (symbol is INamedTypeSymbol type)
        {
            text.Line("skeleton (public API, from metadata):");
            var members = PackageApi.Members(type).ToList();
            for (var index = 0; index < members.Count; index++)
            {
                if (!text.TryLine($"- {SymbolFormatter.Kind(members[index])} {members[index].ToDisplayString(PackageApi.MemberFormat)}"))
                {
                    text.More(members.Count - index, "raise maxTokens to see more members");
                    break;
                }
            }

            return;
        }

        var decompiled = assembly is null ? null : MetadataSource.Decompile(assembly, symbol);
        if (decompiled is null)
        {
            text.Line($"source: not available (no assembly with method bodies found{(assembly is null ? string.Empty : $" for {MetadataSource.Describe(assembly)}")})");
            return;
        }

        text.Line($"source: decompiled from {decompiled.Assembly}");
        text.Line("```cs");
        var lines = decompiled.Code.Split('\n');
        var written = 0;
        foreach (var line in lines)
        {
            if (!text.TryLine(line))
            {
                break;
            }

            written++;
        }

        text.Line("```");
        text.More(lines.Length - written, "source truncated; raise maxTokens");
    }

    private static async Task AppendSourceAsync(ResponseBuilder text, Solution solution, ISymbol symbol, string root, CancellationToken cancellationToken)
    {
        if (symbol is INamedTypeSymbol type)
        {
            text.Line("skeleton:");
            foreach (var entry in OutlineBuilder.ForType(type).Skip(1))
            {
                if (!text.TryLine($"{new string(' ', entry.Depth * 2)}- {entry.Line} {SymbolFormatter.Kind(entry.Symbol)} {entry.Symbol.ToDisplayString(SymbolFormatter.Member)}"))
                {
                    text.More(1, "raise maxTokens to see more members");
                    break;
                }
            }

            return;
        }

        var reference = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (reference is null || solution.GetDocument(reference.SyntaxTree) is not { } document)
        {
            text.Line($"source: not available ({SymbolFormatter.Location(symbol, root)})");
            return;
        }

        // VB declares members on a statement inside the block; take the whole block when they start together.
        var node = await reference.GetSyntaxAsync(cancellationToken);
        if (node.Parent is { } parent && parent.SpanStart == node.SpanStart && parent is not ICompilationUnitSyntax)
        {
            node = parent;
        }

        var source = await document.GetTextAsync(cancellationToken);
        var start = source.Lines.GetLineFromPosition(node.SpanStart).Start;
        var lines = source.ToString(TextSpan.FromBounds(start, node.Span.End)).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var indent = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();

        text.Line(document.Project.Language == LanguageNames.VisualBasic ? "```vb" : "```cs");
        var written = 0;
        foreach (var line in lines)
        {
            if (!text.TryLine(line.Length >= indent ? line[indent..] : line.TrimStart()))
            {
                break;
            }

            written++;
        }

        text.Line("```");
        text.More(lines.Length - written, "source truncated; raise maxTokens");
    }
}
