using Farol.Core.Paths;
using Farol.Core.Text;
using Farol.Engine.Navigation;
using Farol.Engine.Workspaces;
using Microsoft.CodeAnalysis;

namespace Farol.Tools.Features.Navigation;

/// <summary>Shared rendering for navigation results: one line per symbol or hit, grouped by project.</summary>
internal static class NavigationText
{
    public static string Candidate(SymbolCandidate candidate, WorkspaceSnapshot snapshot, string root)
    {
        var frameworks = Frameworks(candidate, snapshot);
        return $"{SymbolFormatter.Display(candidate.Symbol)} · {SymbolFormatter.Location(candidate.Symbol, root)}{frameworks} · id {candidate.Id}";
    }

    public static string Symbol(ISymbol symbol, string root) =>
        $"{SymbolFormatter.Display(symbol)} · {SymbolFormatter.Location(symbol, root)}";

    /// <summary>Hits grouped under project headers, paged by <paramref name="offset"/> over the flat list.</summary>
    public static void Hits(ResponseBuilder text, IReadOnlyList<SourceHit> hits, string root, int offset)
    {
        var start = Math.Clamp(offset, 0, hits.Count);
        string? project = null;
        var index = start;
        for (; index < hits.Count; index++)
        {
            var hit = hits[index];
            if (!string.Equals(project, hit.Project, StringComparison.Ordinal))
            {
                if (!text.TryLine($"{hit.Project}:"))
                {
                    break;
                }

                project = hit.Project;
            }

            var frameworks = hit.TargetFrameworks.Count > 1 ? $" · {string.Join(", ", hit.TargetFrameworks)}" : string.Empty;
            if (!text.TryLine($"- {DisplayPath.Location(root, hit.FilePath, hit.Line)} · {hit.Kind}{frameworks} · {hit.Snippet}"))
            {
                break;
            }
        }

        text.More(hits.Count - index, ResponseBuilder.OffsetHint(index));
    }

    private static string Frameworks(SymbolCandidate candidate, WorkspaceSnapshot snapshot)
    {
        var frameworks = candidate.Variants
            .Select(v => snapshot.Projects.Get(v.ProjectId)?.TargetFramework)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return frameworks.Count > 1 ? $" · {string.Join(", ", frameworks)}" : string.Empty;
    }
}
