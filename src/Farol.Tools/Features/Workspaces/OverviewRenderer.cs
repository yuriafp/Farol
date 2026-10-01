using Farol.Core.Paths;
using Farol.Core.Text;
using Farol.Engine.Analysis;
using Farol.Engine.Toolchain;

namespace Farol.Tools.Features.Workspaces;

internal static class OverviewRenderer
{
    public static string Render(SolutionOverview overview, ToolchainInfo toolchain, string root, int maxTokens)
    {
        var projects = overview.Projects;
        var classic = projects.Count(p => !p.IsSdkStyle);
        var languages = string.Join(", ", projects
            .GroupBy(p => p.Language)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key} {g.Count()}"));

        var text = new ResponseBuilder(maxTokens);
        text.Line($"solution: {DisplayPath.From(root, overview.Target.Path)} · {projects.Count} project(s) · SDK-style {projects.Count - classic}, classic {classic} · {languages}");
        text.Line(WorkspaceStatusRenderer.ToolchainLine(toolchain));
        text.Line($"build: {BuildAdvisor.SuggestBuildCommand(overview, toolchain, root)}");

        var buildSystem = BuildSystemMarkers(overview);
        if (buildSystem.Count > 0)
        {
            text.Line($"build system: {string.Join(" · ", buildSystem)}");
        }

        var legacy = LegacyMarkers(overview, root);
        if (legacy.Count > 0)
        {
            text.Line($"legacy: {string.Join(" · ", legacy)}");
        }

        text.List("projects", projects, p => DescribeProject(p, root), continuation: _ => "raise maxTokens to see all");

        var tests = projects.Where(p => p.TestFrameworks.Count > 0).ToList();
        text.Line(tests.Count == 0
            ? "tests: none detected"
            : $"tests: {string.Join(", ", tests.Select(t => $"{t.Name} ({string.Join("/", t.TestFrameworks)})"))}");

        return text.ToString();
    }

    private static string DescribeProject(ProjectOverview project, string root)
    {
        var parts = new List<string>
        {
            project.Name,
            project.Language,
            project.IsSdkStyle ? "sdk" : "classic",
            project.TargetFrameworks.Count > 0 ? string.Join(";", project.TargetFrameworks) : "tfm unknown",
            project.OutputKind,
        };
        if (project.AppModels.Count > 0)
        {
            parts.Add(string.Join("+", project.AppModels));
        }

        if (project.HasPackagesConfig)
        {
            parts.Add("packages.config");
        }

        if (project.ProjectReferences.Count > 0)
        {
            parts.Add("refs: " + string.Join(", ", project.ProjectReferences));
        }

        parts.Add($"{project.DocumentCount} files");
        parts.Add(DisplayPath.From(root, project.FilePath));
        return string.Join(" · ", parts);
    }

    private static List<string> BuildSystemMarkers(SolutionOverview overview)
    {
        var markers = new List<string>();
        if (overview.CentralPackageManagement)
        {
            markers.Add("central package management (Directory.Packages.props)");
        }

        if (overview.GlobalJsonSdk is { } sdk)
        {
            markers.Add($"global.json pins SDK {sdk}");
        }

        return markers;
    }

    private static List<string> LegacyMarkers(SolutionOverview overview, string root)
    {
        var markers = new List<string>();
        var framework = overview.Projects.Where(p => p.TargetsNetFramework).ToList();
        if (framework.Count == 0)
        {
            return markers;
        }

        markers.Add($".NET Framework in {framework.Count} project(s)");

        var packagesConfig = framework.Count(p => p.HasPackagesConfig);
        if (packagesConfig > 0)
        {
            markers.Add($"packages.config in {packagesConfig}");
        }

        var appModels = framework.SelectMany(p => p.AppModels).Distinct().ToList();
        if (appModels.Count > 0)
        {
            markers.Add("app models: " + string.Join(", ", appModels));
        }

        if (overview.BindingRedirectFiles.Count > 0)
        {
            markers.Add("binding redirects: " + string.Join(", ", overview.BindingRedirectFiles.Select(f => DisplayPath.From(root, f))));
        }

        return markers;
    }
}
