using Farol.Core.Paths;
using Farol.Engine.Navigation;
using Farol.Engine.Workspaces;
using Farol.Tools.Infrastructure;
using Microsoft.CodeAnalysis;

namespace Farol.Tools.Features.Navigation;

/// <summary>A symbol as navigation results write it: display, path:line (or the metadata it comes from) and its id.</summary>
public sealed record SymbolItem(string Symbol, string Kind, string Location, string? Id, IReadOnlyList<string> TargetFrameworks);

/// <summary>One reference or call site: project, path:line, kind, the member it goes through, target frameworks, code.</summary>
public sealed record HitItem(string Project, string Location, string Kind, string? Via, IReadOnlyList<string> TargetFrameworks, string Snippet);

/// <summary>Structured output of dotnet_find_symbols.</summary>
public sealed record FindSymbolsOutput(string Query, string Kind, Page<SymbolItem> Symbols)
{
    /// <summary>Set when the answer comes from the persistent index while the workspace loads.</summary>
    public IndexNote? Index { get; init; }
}

/// <summary>Structured output of dotnet_find_references. <see cref="Candidates"/> is set instead when the name is ambiguous.</summary>
public sealed record FindReferencesOutput(
    SymbolItem? Symbol,
    int Total,
    int Projects,
    IReadOnlyList<string> DefinedAt,
    IReadOnlyList<ThroughItem> Through,
    Page<HitItem>? References,
    IReadOnlyList<SymbolItem>? Candidates)
{
    /// <summary>Set when the answer comes from the persistent index while the workspace loads.</summary>
    public IndexNote? Index { get; init; }
}

public sealed record ThroughItem(SymbolItem Member, string Via, int References);

/// <summary>Structured output of dotnet_hierarchy.</summary>
public sealed record HierarchyOutput(SymbolItem? Symbol, string Direction, Page<RelatedItem>? Related, IReadOnlyList<SymbolItem>? Candidates)
{
    /// <summary>Set when the answer comes from the persistent index while the workspace loads.</summary>
    public IndexNote? Index { get; init; }
}

public sealed record RelatedItem(string Relation, SymbolItem Symbol);

/// <summary>Structured output of dotnet_call_hierarchy: the tree as far as the text shows it, in text order, each node naming its parent's index.</summary>
public sealed record CallHierarchyOutput(SymbolItem? Symbol, string Direction, int Depth, IReadOnlyList<CallItem>? Calls, int Omitted, IReadOnlyList<SymbolItem>? Candidates)
{
    /// <summary>Set when the answer comes from the persistent index while the workspace loads.</summary>
    public IndexNote? Index { get; init; }
}

public sealed record CallItem(int Depth, int? Parent, SymbolItem Symbol, IReadOnlyList<string> Sites);

/// <summary>Structured output of dotnet_outline.</summary>
public sealed record OutlineOutput(string Title, int Total, IReadOnlyList<OutlineItem>? Declarations, IReadOnlyList<SymbolItem>? Candidates)
{
    /// <summary>Set when the answer comes from the persistent index while the workspace loads.</summary>
    public IndexNote? Index { get; init; }
}

public sealed record OutlineItem(int Depth, string Kind, string Signature, string Location);

/// <summary>Structured output of dotnet_symbol.</summary>
public sealed record SymbolOutput(
    SymbolItem? Symbol,
    string? Namespace,
    string? Project,
    string? Assembly,
    IReadOnlyList<string> DeclaredAt,
    string? DeclaredIn,
    IReadOnlyList<string> Inherits,
    string? Summary,
    string? Source,
    string? SourceKind,
    IReadOnlyList<SymbolItem>? Candidates)
{
    /// <summary>Set when the answer comes from the persistent index while the workspace loads.</summary>
    public IndexNote? Index { get; init; }
}

internal static class NavigationItems
{
    public static SymbolItem Of(SymbolCandidate candidate, WorkspaceSnapshot snapshot, string root) => new(
        candidate.Symbol.ToDisplayString(SymbolFormatter.Signature),
        SymbolFormatter.Kind(candidate.Symbol),
        SymbolFormatter.Location(candidate.Symbol, root),
        candidate.Id,
        [.. candidate.Variants
            .Select(v => snapshot.Projects.Get(v.ProjectId)?.TargetFramework)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)]);

    public static SymbolItem Of(ISymbol symbol, string root) => new(
        symbol.ToDisplayString(SymbolFormatter.Signature),
        SymbolFormatter.Kind(symbol),
        SymbolFormatter.Location(symbol, root),
        symbol.GetDocumentationCommentId(),
        []);

    public static HitItem Of(SourceHit hit, string root) =>
        new(hit.Project, DisplayPath.Location(root, hit.FilePath, hit.Line), hit.Kind, hit.Via, hit.TargetFrameworks, hit.Snippet);
}
