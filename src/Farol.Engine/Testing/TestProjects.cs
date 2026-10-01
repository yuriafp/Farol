using Farol.Engine.Workspaces;
using Microsoft.CodeAnalysis;

namespace Farol.Engine.Testing;

/// <summary>A test project: its project file, format, the test frameworks it uses and its target-framework variants.</summary>
public sealed record TestProjectInfo(string Name, string FilePath, bool IsSdkStyle, IReadOnlyList<string> Frameworks, IReadOnlyList<ProjectId> Variants);

/// <summary>Finds test projects and the test methods they declare.</summary>
public static class TestProjects
{
    /// <summary>
    /// Projects that use a test framework, from resolved references or from the project file itself (so a classic
    /// MSTest project counts before its packages.config is restored).
    /// </summary>
    public static IReadOnlyList<TestProjectInfo> Find(WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var found = new List<TestProjectInfo>();
        foreach (var variants in snapshot.Solution.Projects.Where(p => p.FilePath is not null).GroupBy(p => p.FilePath!, StringComparer.OrdinalIgnoreCase))
        {
            var facts = ProjectFileInspector.Inspect(variants.Key);
            var resolved = variants
                .SelectMany(p => p.MetadataReferences)
                .OfType<PortableExecutableReference>()
                .Select(r => Path.GetFileNameWithoutExtension(r.FilePath))
                .OfType<string>();
            var frameworks = TestFrameworks.Detect(resolved.Concat(facts.References), facts.Sdk);
            if (frameworks.Count == 0 && facts.ProjectTypeGuids.Contains(ProjectTypeGuids.Test))
            {
                frameworks = [TestFrameworks.MSTest];
            }

            if (frameworks.Count > 0)
            {
                var name = snapshot.Projects.Get(variants.First().Id)?.Name ?? Path.GetFileNameWithoutExtension(variants.Key);
                found.Add(new TestProjectInfo(name, variants.Key, facts.IsSdkStyle, frameworks, [.. variants.Select(p => p.Id)]));
            }
        }

        return [.. found.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Fully qualified names of the test methods declared in a test project (any target-framework variant).</summary>
    public static async Task<IReadOnlyList<string>> TestNamesAsync(Solution solution, TestProjectInfo project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(project);
        if (solution.GetProject(project.Variants[0]) is not { } variant || await variant.GetCompilationAsync(cancellationToken) is not { } compilation)
        {
            return [];
        }

        var names = new SortedSet<string>(StringComparer.Ordinal);
        var pending = new Stack<INamespaceOrTypeSymbol>([compilation.Assembly.GlobalNamespace]);
        while (pending.Count > 0)
        {
            foreach (var member in pending.Pop().GetMembers())
            {
                switch (member)
                {
                    case INamespaceOrTypeSymbol container:
                        pending.Push(container);
                        break;
                    case IMethodSymbol method when TestFrameworks.IsTest(method):
                        names.Add(TestFrameworks.FullyQualifiedName(method));
                        break;
                }
            }
        }

        return [.. names];
    }
}
