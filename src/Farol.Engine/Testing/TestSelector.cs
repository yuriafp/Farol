using Farol.Core;
using Farol.Engine.Navigation;
using Farol.Engine.Workspaces;

namespace Farol.Engine.Testing;

/// <summary>The tests to run and, in words, why those.</summary>
public sealed record TestSelection(IReadOnlyList<TestTarget> Targets, string Description);

/// <summary>Turns "all", "names containing", "affected by a symbol" or "affected by my changes" into test targets.</summary>
public static class TestSelector
{
    /// <param name="affected">Where to start for affected tests (null: not asked), and how to name it in the description.</param>
    public static async Task<TestSelection> SelectAsync(
        WorkspaceSnapshot snapshot, string? project, string? nameContains, (IReadOnlyList<SymbolVariant> Roots, string Subject)? affected, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var all = TestProjects.Find(snapshot);
        if (all.Count == 0)
        {
            throw new FarolException(ErrorCodes.InvalidArgument, "The workspace has no test projects.", "Test projects reference xUnit, NUnit, MSTest or TUnit.");
        }

        var projects = project is null ? all : [.. all.Where(p => p.Name.Equals(project.Trim(), StringComparison.OrdinalIgnoreCase))];
        if (projects.Count == 0)
        {
            throw new FarolException(ErrorCodes.InvalidArgument, $"No test project named '{project}'.", $"Test projects: {string.Join(", ", all.Select(p => p.Name))}.");
        }

        var contains = string.IsNullOrWhiteSpace(nameContains) ? null : nameContains.Trim();
        if (affected is not { } start)
        {
            var scope = projects.Count == 1 ? projects[0].Name : $"{projects.Count} test projects";
            return new TestSelection(
                [.. projects.Select(p => new TestTarget(p, null, contains))],
                contains is null ? $"all tests in {scope}" : $"tests whose name contains '{contains}' in {scope}");
        }

        if (start.Roots.Count == 0)
        {
            return new TestSelection([], $"nothing to start from: {start.Subject}");
        }

        var found = await AffectedTestFinder.FindAsync(snapshot, projects, start.Roots, cancellationToken);
        if (!found.Complete)
        {
            // Too many paths to follow: every test project that can reach the code runs in full.
            var graph = snapshot.Solution.GetProjectDependencyGraph();
            var rootProjects = start.Roots.Select(r => r.ProjectId).OfType<Microsoft.CodeAnalysis.ProjectId>().ToHashSet();
            var reaching = projects.Where(p => p.Variants.Any(v => rootProjects.Contains(v) || graph.GetProjectsThatThisProjectTransitivelyDependsOn(v).Overlaps(rootProjects))).ToList();
            return new TestSelection(
                [.. reaching.Select(p => new TestTarget(p, null, contains))],
                $"the reference search from {start.Subject} grew too large: every test in the {reaching.Count} test project(s) that depend on it");
        }

        var targets = new List<TestTarget>();
        var parts = new List<string>();
        foreach (var testProject in projects)
        {
            if (!found.TestsByProject.TryGetValue(testProject.FilePath, out var names))
            {
                continue;
            }

            var selected = contains is null ? names : [.. names.Where(n => n.Contains(contains, StringComparison.OrdinalIgnoreCase))];
            if (selected.Count == 0)
            {
                continue;
            }

            var total = (await TestProjects.TestNamesAsync(snapshot.Solution, testProject, cancellationToken)).Count;
            targets.Add(new TestTarget(testProject, selected));
            parts.Add($"{selected.Count} of {total} test method(s) in {testProject.Name}");
        }

        return new TestSelection(
            targets,
            targets.Count == 0 ? $"no test reaches {start.Subject}" : $"{string.Join(", ", parts)} reach {start.Subject}");
    }
}
