using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Farol.Engine.Workspaces;
using Microsoft.CodeAnalysis;

namespace Farol.Engine.Analysis;

/// <summary>Builds the one-call map of a loaded solution that agents need before touching any code.</summary>
public static class SolutionOverviewBuilder
{
    public static SolutionOverview Build(WorkspaceTarget target, Solution solution, LoadReport? report)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(solution);

        // Multi-targeted projects appear once per target framework: group them by project file.
        var projects = solution.Projects
            .Where(p => p.FilePath is not null)
            .GroupBy(p => p.FilePath!, StringComparer.OrdinalIgnoreCase)
            .Select(g => DescribeProject(g.Key, [.. g], solution, report))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SolutionOverview(
            target,
            projects,
            UsesCentralPackageManagement(target.Directory),
            ReadGlobalJsonSdk(target.Directory),
            FindBindingRedirects(projects));
    }

    private static ProjectOverview DescribeProject(string path, List<Project> variants, Solution solution, LoadReport? report)
    {
        var facts = ProjectFileInspector.Inspect(path);
        var primary = variants[0];
        var references = variants
            .SelectMany(v => v.MetadataReferences)
            .OfType<PortableExecutableReference>()
            .Select(r => Path.GetFileNameWithoutExtension(r.FilePath))
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var projectReferences = variants
            .SelectMany(v => v.ProjectReferences)
            .Select(r => solution.GetProject(r.ProjectId))
            .OfType<Project>()
            .Select(p => ProjectNames.StripTargetFramework(p.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ProjectOverview(
            ProjectNames.StripTargetFramework(primary.Name),
            path,
            primary.Language == LanguageNames.VisualBasic ? "VB" : primary.Language,
            facts.IsSdkStyle,
            ResolveTargetFrameworks(path, variants, facts, report),
            DescribeOutputKind(primary.CompilationOptions?.OutputKind),
            AppModelDetector.DetectAppModels(references, Path.GetDirectoryName(path)!, facts),
            Testing.TestFrameworks.Detect(references.Concat(facts.References), facts.Sdk),
            projectReferences,
            variants.SelectMany(v => v.Documents).Select(d => d.FilePath ?? d.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            facts.HasPackagesConfig);
    }

    // Prefer what MSBuild reported while loading; then the "Name(tfm)" suffix; then the project file itself.
    private static IReadOnlyList<string> ResolveTargetFrameworks(string path, List<Project> variants, ProjectFileFacts facts, LoadReport? report)
    {
        if (report is not null && report.TargetFrameworks.TryGetValue(path, out var loaded) && loaded.Count > 0)
        {
            return loaded;
        }

        var fromNames = variants.Select(v => ProjectNames.TargetFrameworkSuffix(v.Name)).OfType<string>().Order(StringComparer.OrdinalIgnoreCase).ToList();
        return fromNames.Count > 0 ? fromNames : facts.DeclaredTargetFrameworks;
    }

    private static string DescribeOutputKind(OutputKind? kind) => kind switch
    {
        OutputKind.ConsoleApplication => "exe",
        OutputKind.WindowsApplication => "winexe",
        OutputKind.DynamicallyLinkedLibrary => "library",
        OutputKind.NetModule => "module",
        OutputKind.WindowsRuntimeMetadata => "winmd",
        OutputKind.WindowsRuntimeApplication => "winrt-exe",
        _ => "unknown",
    };

    private static bool UsesCentralPackageManagement(string directory)
    {
        if (FindUpwards(directory, "Directory.Packages.props") is not { } props)
        {
            return false;
        }

        try
        {
            return XDocument.Load(props).Descendants()
                .Any(e => e.Name.LocalName == "ManagePackageVersionsCentrally"
                          && string.Equals(e.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or XmlException)
        {
            return false;
        }
    }

    private static string? ReadGlobalJsonSdk(string directory)
    {
        if (FindUpwards(directory, "global.json") is not { } globalJson)
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(globalJson), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return json.RootElement.TryGetProperty("sdk", out var sdk) && sdk.TryGetProperty("version", out var version) ? version.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private static List<string> FindBindingRedirects(IEnumerable<ProjectOverview> projects)
    {
        var files = new List<string>();
        foreach (var project in projects.Where(p => p.TargetsNetFramework))
        {
            // The files as they are named on disk (Web.config), not as Windows would also accept them (web.config).
            foreach (var path in Legacy.ConfigInspector.FindIn(Path.GetDirectoryName(project.FilePath)!))
            {
                if (File.ReadAllText(path).Contains("bindingRedirect", StringComparison.OrdinalIgnoreCase))
                {
                    files.Add(path);
                }
            }
        }

        return files;
    }

    private static string? FindUpwards(string directory, string fileName)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
