using Farol.Core.Paths;
using Farol.Engine.Analysis;
using NuGet.Frameworks;

namespace Farol.Engine.Legacy;

/// <summary>One project's move to the target: how, how big, after which projects, and the concrete tasks.</summary>
public sealed record MigrationStep(
    int Number,
    string Project,
    string FilePath,
    string Kind,
    string From,
    string To,
    string Approach,
    string Size,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<string> Tasks);

public sealed record MigrationPlan(
    string Workspace,
    string Target,
    IReadOnlyList<string> Strategy,
    IReadOnlyList<MigrationStep> Steps,
    IReadOnlyList<string> AlreadyModern,
    LegacyInventoryReport Inventory,
    PortabilityReport Portability,
    IReadOnlyList<ConfigReport> Configs);

/// <summary>
/// Orders the migration bottom-up by project dependency — a project moves after everything it references, so it can be
/// built and tested on the target right away — and derives each step's tasks from the inventory, the portability
/// findings and the configuration files. Libraries multi-target during the migration; System.Web applications are
/// rebuilt on ASP.NET Core incrementally rather than converted.
/// </summary>
public static class MigrationPlanner
{
    private const int MaxListed = 4;

    private static readonly HashSet<string> ConfigUnits = new(StringComparer.Ordinal) { "app setting", "connection string", "binding redirect" };

    public static MigrationPlan Build(string workspace, LegacyInventoryReport inventory, PortabilityReport portability, NuGetFramework target, string root)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(portability);
        ArgumentNullException.ThrowIfNull(target);
        var targetName = target.GetShortFolderName();
        var legacy = inventory.Projects.Where(p => p.Overview.TargetsNetFramework).ToList();
        var alreadyModern = inventory.Projects.Where(p => !p.Overview.TargetsNetFramework).Select(p => p.Overview.Name).ToList();
        var configs = legacy.SelectMany(p => p.ConfigFiles).Select(TryRead).OfType<ConfigReport>().ToList();

        var names = legacy.Select(p => p.Overview.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int Level(ProjectInventory project, HashSet<string> visiting)
        {
            if (levels.TryGetValue(project.Overview.Name, out var known))
            {
                return known;
            }

            if (!visiting.Add(project.Overview.Name))
            {
                return 0; // A reference cycle: MSBuild would reject it anyway.
            }

            var level = project.Overview.ProjectReferences
                .Select(r => legacy.FirstOrDefault(p => p.Overview.Name.Equals(r, StringComparison.OrdinalIgnoreCase)))
                .OfType<ProjectInventory>()
                .Select(r => Level(r, visiting) + 1)
                .DefaultIfEmpty(0)
                .Max();
            levels[project.Overview.Name] = level;
            return level;
        }

        var ordered = legacy
            .OrderBy(p => Level(p, new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
            .ThenBy(p => Rank(KindOf(p.Overview)))
            .ThenBy(p => p.Overview.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var steps = new List<MigrationStep>();
        foreach (var project in ordered)
        {
            steps.Add(Step(steps.Count + 1, project, inventory, portability, configs, target, root, names));
        }

        var strategy = new List<string>
        {
            $"Bottom-up by project dependency: each project moves after everything it references, so it builds and runs its tests on {targetName} right away.",
            target.Framework == FrameworkConstants.FrameworkIdentifiers.NetStandard
                ? $"Libraries move to {targetName}, which the .NET Framework applications can still reference while they wait for their turn."
                : $"Libraries and test projects target their .NET Framework version and {targetName} side by side during the migration, so the applications that still run on .NET Framework keep using them unchanged.",
            "Applications move last. Web applications built on System.Web cannot be converted: an ASP.NET Core application grows next to them and takes their endpoints over one at a time.",
            "After each step, run dotnet_portability for the project: its list should be empty before the next step starts.",
        };

        return new MigrationPlan(workspace, targetName, strategy, steps, alreadyModern, inventory, portability, configs);
    }

    public static string KindOf(ProjectOverview project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var executable = project.OutputKind is "exe" or "winexe";
        if (project.TestFrameworks.Count > 0)
        {
            return "test project";
        }

        if (project.AppModels.Any(m => m is "WebForms" or "ASP.NET MVC 5" or "ASP.NET Web API 2" or "ASMX" or "WCF service"))
        {
            return "web application";
        }

        if (project.AppModels.Any(m => m is "WinForms" or "WPF"))
        {
            return executable ? "desktop application" : "desktop library";
        }

        return executable ? "application" : "library";
    }

    private static int Rank(string kind) => kind switch
    {
        "library" or "desktop library" => 0,
        "test project" => 1,
        "application" or "desktop application" => 2,
        _ => 3,
    };

    private static MigrationStep Step(
        int number, ProjectInventory project, LegacyInventoryReport inventory, PortabilityReport portability, IReadOnlyList<ConfigReport> configs,
        NuGetFramework target, string root, HashSet<string> legacyNames)
    {
        var overview = project.Overview;
        var kind = KindOf(overview);
        var from = string.Join(";", overview.TargetFrameworks.Where(TargetFrameworkNames.IsNetFramework).DefaultIfEmpty("net4x"));
        var targetName = target.GetShortFolderName();
        var netStandard = target.Framework == FrameworkConstants.FrameworkIdentifiers.NetStandard;
        var windows = string.Equals(target.Platform, "windows", StringComparison.OrdinalIgnoreCase) ? targetName : $"{targetName}-windows";
        var (to, approach) = kind switch
        {
            "library" or "test project" when netStandard => (targetName, $"Convert to SDK-style and target {targetName}, which .NET Framework and .NET both load."),
            "library" => ($"{from};{targetName}", $"Convert to SDK-style and multi-target {from};{targetName}, so the .NET Framework projects that use it keep building while they migrate."),
            "test project" => ($"{from};{targetName}", $"Convert to SDK-style and multi-target {from};{targetName}: the same tests then check both builds of what they test."),
            "desktop library" or "desktop application" => (windows, $"Convert to SDK-style and retarget to {windows}: WinForms and WPF run on .NET on Windows."),
            "web application" => ($"a new ASP.NET Core project ({targetName})", "System.Web does not exist on .NET: build an ASP.NET Core application next to this one and move endpoints over incrementally, then retire this project."),
            _ => (targetName, $"Convert to SDK-style and retarget to {targetName}."),
        };

        var tasks = new List<string>();
        var web = kind == "web application";
        if (!overview.IsSdkStyle && !web)
        {
            tasks.Add($"Convert {Path.GetFileName(overview.FilePath)} to an SDK-style project, still targeting {from} (dotnet upgrade-assistant does it; by hand, delete the Compile items: SDK-style projects include every source file).");
        }

        if (project.PackagesConfigPackages > 0 && !web)
        {
            tasks.Add($"Move packages.config ({project.PackagesConfigPackages} package(s)) to PackageReference (Visual Studio: right-click packages.config › Migrate packages.config to PackageReference).");
        }

        tasks.Add(kind switch
        {
            "library" or "test project" when netStandard => $"Set `<TargetFramework>{targetName}</TargetFramework>`.",
            "library" or "test project" => $"Set `<TargetFrameworks>{from};{targetName}</TargetFrameworks>`.",
            "desktop library" or "desktop application" => $"Set `<TargetFramework>{windows}</TargetFramework>` with {(overview.AppModels.Contains("WPF") ? "`<UseWPF>true</UseWPF>`" : "`<UseWindowsForms>true</UseWindowsForms>`")}.",
            "web application" => $"Create an ASP.NET Core project on {targetName} and put YARP in front of both applications, so requests move endpoint by endpoint; Microsoft.AspNetCore.SystemWebAdapters shares session and authentication between them.",
            _ => $"Set `<TargetFramework>{targetName}</TargetFramework>`.",
        });

        tasks.AddRange(TechnologyTasks(overview.Name, inventory, portability, root));
        tasks.AddRange(ConfigTasks(project, configs, root));
        tasks.Add(kind switch
        {
            "library" or "test project" when !netStandard => $"Build both target frameworks with dotnet_build and run the tests with dotnet_test on each; dotnet_portability project={overview.Name} must report nothing.",
            "web application" => $"Keep both applications running until {overview.Name} serves no endpoint anymore, then remove it from the solution.",
            _ => $"Build with dotnet_build, run the tests, and check that dotnet_portability project={overview.Name} reports nothing.",
        });

        var issues = portability.IssuesOf(overview.Name).ToList();
        var rewrite = web || issues.Any(i => i.Technology?.IsRewrite == true) || inventory.ItemsOf(overview.Name).Any(i => LegacyCatalog.Technologies.FirstOrDefault(t => t.Name == i.Technology)?.IsRewrite == true);
        var size = rewrite ? "L" : issues.Count > 0 || project.PackagesConfigPackages > 0 ? "M" : "S";
        var dependsOn = overview.ProjectReferences.Where(legacyNames.Contains).ToList();
        return new MigrationStep(number, overview.Name, overview.FilePath, kind, from, to, approach, size, dependsOn, tasks);
    }

    /// <summary>
    /// One task per legacy technology in the project: the markup files and code uses the inventory found, the APIs the
    /// target lacks, and the replacement. Desktop technologies only get a task when the target drops some of their APIs.
    /// </summary>
    private static List<string> TechnologyTasks(string project, LegacyInventoryReport inventory, PortabilityReport portability, string root)
    {
        var items = inventory.ItemsOf(project).Where(i => LegacyCatalog.Technologies.Any(t => t.Name == i.Technology)).ToList();
        var issues = portability.IssuesOf(project).ToList();
        var technologies = items.Select(i => i.Technology)
            .Concat(issues.Select(i => i.Technology?.Name ?? Other(i)))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var tasks = new List<(int Order, string Text)>();
        foreach (var name in technologies)
        {
            var technology = LegacyCatalog.Technologies.FirstOrDefault(t => t.Name == name);
            var own = issues.Where(i => (i.Technology?.Name ?? Other(i)) == name).ToList();
            if (technology?.RunsOnWindowsDesktop == true && own.Count == 0)
            {
                continue;
            }

            var facts = new List<string>();
            var item = items.FirstOrDefault(i => i.Technology == name);
            if (item is not null)
            {
                var files = item.Counts.Where(c => !ConfigUnits.Contains(c.Unit) && c.Unit != "code use").ToList();
                if (files.Count > 0)
                {
                    var paths = item.Locations.Where(l => !l.Contains(".cs:", StringComparison.OrdinalIgnoreCase) && !l.Contains(".vb:", StringComparison.OrdinalIgnoreCase)).ToList();
                    facts.Add($"{string.Join(", ", files)}{List(paths, root)}");
                }
            }

            if (own.Count > 0)
            {
                var places = own.SelectMany(i => i.Api.Locations).Distinct().OrderBy(l => l.FilePath, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.Line).ToList();
                var apis = own.Select(i => i.Api.Display).Distinct(StringComparer.Ordinal).ToList();
                facts.Add($"{apis.Count} API(s) missing or obsolete on {portability.Target} — {Join(apis)} — at {DisplayPath.Places(root, places.Select(l => $"{l.FilePath}:{l.Line}"), MaxListed)}");
            }
            else if (item?.Counts.FirstOrDefault(c => c.Unit == "code use") is { } uses)
            {
                var places = item.Locations.Where(l => l.Contains(".cs:", StringComparison.OrdinalIgnoreCase) || l.Contains(".vb:", StringComparison.OrdinalIgnoreCase)).ToList();
                facts.Add($"{uses}{List(places, root)}");
            }

            if (facts.Count == 0)
            {
                continue;
            }

            var replacement = technology?.Replacement ?? "Find the replacement API for the target framework (the .NET API browser lists where each API went).";
            tasks.Add((technology?.IsRewrite == true ? 0 : 1, $"{name}: {string.Join("; ", facts)}. → {replacement}"));
        }

        return [.. tasks.OrderBy(t => t.Order).Select(t => t.Text)];
    }

    private static List<string> ConfigTasks(ProjectInventory project, IReadOnlyList<ConfigReport> configs, string root)
    {
        var tasks = new List<string>();
        foreach (var config in configs.Where(c => project.ConfigFiles.Contains(c.FilePath, StringComparer.OrdinalIgnoreCase)))
        {
            var path = DisplayPath.From(root, config.FilePath);
            if (config.AppSettings.Count + config.ConnectionStrings.Count > 0)
            {
                var secrets = config.MaskedCount > 0 ? $", keeping its {config.MaskedCount} secret(s) in user secrets or environment variables instead" : string.Empty;
                tasks.Add($"Move {config.AppSettings.Count} app setting(s) and {config.ConnectionStrings.Count} connection string(s) from {path} to appsettings.json (dotnet_config_inspect path={path} prints the mapping){secrets}.");
            }

            if (config.BindingRedirects.Count > 0)
            {
                tasks.Add($"Drop the {config.BindingRedirects.Count} binding redirect(s) in {path}: .NET unifies assembly versions by itself.");
            }
        }

        return tasks;
    }

    private static string Other(PortabilityIssue issue) => $"Other ({issue.Api.Namespace})";

    private static string List(List<string> paths, string root) =>
        paths.Count == 0 ? string.Empty : $" ({DisplayPath.Places(root, paths, MaxListed)})";

    private static string Join(List<string> values) =>
        values.Count <= MaxListed ? string.Join(", ", values) : $"{string.Join(", ", values.Take(MaxListed))} and {values.Count - MaxListed} more";

    private static ConfigReport? TryRead(string path)
    {
        try
        {
            return ConfigInspector.Read(path);
        }
        catch (Farol.Core.FarolException)
        {
            return null;
        }
    }
}
