using Farol.Engine.Analysis;
using Farol.Engine.Workspaces;
using Microsoft.CodeAnalysis;

namespace Farol.Engine.Legacy;

public sealed record InventoryCount(int Count, string Unit)
{
    public override string ToString() => $"{Count} {Unit}{(Count == 1 || Unit.EndsWith('s') ? string.Empty : "s")}";
}

/// <summary>One technology in one project: how much of it there is and where (files, or file and line for code uses).</summary>
public sealed record InventoryItem(string Technology, string Area, string Project, IReadOnlyList<InventoryCount> Counts, IReadOnlyList<string> Locations);

/// <summary>A file with the most uses of legacy APIs in its project: where migration work concentrates.</summary>
public sealed record Hotspot(string FilePath, int Uses, IReadOnlyList<string> Technologies);

public sealed record ProjectInventory(
    ProjectOverview Overview,
    int SourceFiles,
    int PackagesConfigPackages,
    IReadOnlyList<string> ConfigFiles,
    IReadOnlyList<Hotspot> Hotspots);

public sealed record LegacyInventoryReport(IReadOnlyList<ProjectInventory> Projects, IReadOnlyList<InventoryItem> Items)
{
    public IEnumerable<InventoryItem> ItemsOf(string project) => Items.Where(i => i.Project.Equals(project, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// What is legacy in a solution and where: project formats and frameworks, app models found in markup and code,
/// configuration, and the uses of legacy APIs (BinaryFormatter, System.Configuration, System.Web…) with their lines.
/// </summary>
public static class LegacyInventory
{
    private const int MaxHotspots = 3;
    private const int MaxFilesScanned = 20_000;

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "packages", "node_modules", ".vs", ".git" };

    /// <summary>Markup and content files that belong to a technology, by extension: technology, unit.</summary>
    private static readonly Dictionary<string, (string Technology, string Unit)> MarkupFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        [".aspx"] = ("WebForms", "page"),
        [".ascx"] = ("WebForms", "user control"),
        [".master"] = ("WebForms", "master page"),
        [".asmx"] = ("ASMX web services", "service"),
        [".svc"] = ("WCF", "service"),
        [".ashx"] = ("ASP.NET (System.Web)", "HTTP handler"),
        [".asax"] = ("ASP.NET (System.Web)", "application file"),
    };

    public static async Task<LegacyInventoryReport> BuildAsync(WorkspaceSnapshot snapshot, WorkspaceTarget target, LoadReport? report, string? projectName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(target);
        var overview = SolutionOverviewBuilder.Build(target, snapshot.Solution, report);
        var projects = new List<ProjectInventory>();
        var items = new List<InventoryItem>();
        foreach (var project in overview.Projects.Where(p => projectName is null || p.Name.Equals(projectName.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (inventory, found) = await DescribeAsync(snapshot, project, cancellationToken);
            projects.Add(inventory);
            items.AddRange(found);
        }

        return new LegacyInventoryReport(projects, items);
    }

    /// <summary>The Roslyn project to analyze for a project file: its .NET Framework build when it has one, the first one otherwise.</summary>
    public static Project? Primary(WorkspaceSnapshot snapshot, string projectFile)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var variants = snapshot.Solution.Projects.Where(p => string.Equals(p.FilePath, projectFile, StringComparison.OrdinalIgnoreCase)).ToList();
        return variants.FirstOrDefault(v => snapshot.Projects.Get(v.Id)?.TargetFramework is { } tfm && TargetFrameworkNames.IsNetFramework(tfm)) ?? variants.FirstOrDefault();
    }

    private static async Task<(ProjectInventory Inventory, List<InventoryItem> Items)> DescribeAsync(WorkspaceSnapshot snapshot, ProjectOverview project, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(project.FilePath)!;
        var items = new Dictionary<string, (string Area, List<InventoryCount> Counts, List<string> Locations)>(StringComparer.Ordinal);
        void Add(string technology, string area, InventoryCount count, IEnumerable<string> locations)
        {
            if (!items.TryGetValue(technology, out var item))
            {
                item = (area, [], []);
                items[technology] = item;
            }

            item.Counts.Add(count);
            item.Locations.AddRange(locations);
        }

        // Markup: WebForms pages, ASMX and WCF endpoints, handlers, Global.asax; WPF XAML and MVC views when the project uses them.
        foreach (var group in ScanFiles(directory).GroupBy(f => Classify(f, project)).Where(g => g.Key is not null))
        {
            var (technology, unit) = group.Key!.Value;
            Add(technology, LegacyCatalog.Named(technology).Area, new InventoryCount(group.Count(), unit), group);
        }

        var roslyn = Primary(snapshot, project.FilePath);
        var compilation = roslyn is null ? null : await roslyn.GetCompilationAsync(cancellationToken);
        if (compilation is not null)
        {
            foreach (var (unit, types) in WinFormsTypes(compilation))
            {
                Add("WinForms", "Desktop", new InventoryCount(types.Count, unit), types);
            }
        }

        // Configuration files.
        var configs = ConfigInspector.FindIn(directory);
        foreach (var config in configs)
        {
            ConfigReport read;
            try
            {
                read = ConfigInspector.Read(config);
            }
            catch (Farol.Core.FarolException)
            {
                continue;
            }

            if (read.BindingRedirects.Count > 0)
            {
                Add("binding redirects", "Configuration", new InventoryCount(read.BindingRedirects.Count, "binding redirect"), [config]);
            }

            if (read.AppSettings.Count > 0)
            {
                Add("System.Configuration", "Configuration", new InventoryCount(read.AppSettings.Count, "app setting"), [config]);
            }

            if (read.ConnectionStrings.Count > 0)
            {
                Add("System.Configuration", "Configuration", new InventoryCount(read.ConnectionStrings.Count, "connection string"), [config]);
            }

            if (read.ServiceModel is { } wcf && wcf.Services.Count + wcf.Clients.Count > 0)
            {
                if (wcf.Services.Count > 0)
                {
                    Add("WCF", "Services", new InventoryCount(wcf.Services.Count, "configured service"), [config]);
                }

                if (wcf.Clients.Count > 0)
                {
                    Add("WCF", "Services", new InventoryCount(wcf.Clients.Count, "client endpoint"), [config]);
                }
            }
        }

        // Code: uses of legacy APIs, by technology, with their lines. WinForms and WPF code runs on .NET (-windows):
        // their forms and XAML files are counted instead of every API call.
        var hotspots = new List<Hotspot>();
        if (roslyn is not null)
        {
            var uses = (await ApiUsageScanner.ScanAsync(roslyn, cancellationToken))
                .Where(u => u.Technology is { RunsOnWindowsDesktop: false })
                .SelectMany(u => u.Locations.Select(l => (Technology: u.Technology!.Name, Area: u.Technology.Area, Location: l)))
                .ToList();
            foreach (var technology in uses.GroupBy(u => (u.Technology, u.Area)))
            {
                var lines = technology.Select(u => u.Location).Distinct().OrderBy(l => l.FilePath, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.Line).ToList();
                Add(technology.Key.Technology, technology.Key.Area, new InventoryCount(lines.Count, "code use"), lines.Select(l => $"{l.FilePath}:{l.Line}"));
            }

            hotspots = [.. uses
                .GroupBy(u => u.Location.FilePath, StringComparer.OrdinalIgnoreCase)
                .Select(g => new Hotspot(g.Key, g.Select(u => u.Location.Line).Distinct().Count(), [.. g.Select(u => u.Technology).Distinct().Order(StringComparer.Ordinal)]))
                .OrderByDescending(h => h.Uses)
                .ThenBy(h => h.FilePath, StringComparer.OrdinalIgnoreCase)
                .Take(MaxHotspots)];
        }

        var packagesConfig = project.HasPackagesConfig ? CountPackages(Path.Combine(directory, "packages.config")) : 0;
        var sourceFiles = roslyn?.Documents.Count(d => d.FilePath is not null && !ApiUsageScanner.IsGenerated(d.FilePath)) ?? project.DocumentCount;
        var inventory = new ProjectInventory(project, sourceFiles, packagesConfig, configs, hotspots);
        return (inventory, [.. items.Select(kv => new InventoryItem(kv.Key, kv.Value.Area, project.Name, kv.Value.Counts, [.. kv.Value.Locations.Distinct(StringComparer.OrdinalIgnoreCase)]))]);
    }

    private static (string Technology, string Unit)? Classify(string file, ProjectOverview project)
    {
        var extension = Path.GetExtension(file);
        if (MarkupFiles.TryGetValue(extension, out var known))
        {
            return known;
        }

        if (extension.Equals(".xaml", StringComparison.OrdinalIgnoreCase) && project.AppModels.Contains("WPF"))
        {
            return ("WPF", "XAML file");
        }

        if ((extension.Equals(".cshtml", StringComparison.OrdinalIgnoreCase) || extension.Equals(".vbhtml", StringComparison.OrdinalIgnoreCase)) && project.AppModels.Contains("ASP.NET MVC 5"))
        {
            return ("ASP.NET MVC 5", "view");
        }

        return null;
    }

    /// <summary>Forms and user controls declared in the project's own code (one location per type, preferring the non-designer file).</summary>
    private static List<(string Unit, List<string> Types)> WinFormsTypes(Compilation compilation)
    {
        var form = compilation.GetTypeByMetadataName("System.Windows.Forms.Form");
        var control = compilation.GetTypeByMetadataName("System.Windows.Forms.UserControl");
        if (form is null)
        {
            return [];
        }

        var forms = new List<string>();
        var controls = new List<string>();
        foreach (var type in compilation.GetSymbolsWithName(static _ => true, SymbolFilter.Type).OfType<INamedTypeSymbol>())
        {
            var target = DerivesFrom(type, form) ? forms : control is not null && DerivesFrom(type, control) ? controls : null;
            var location = type.Locations.Where(l => l.IsInSource).OrderBy(l => ApiUsageScanner.IsGenerated(l.SourceTree?.FilePath) ? 1 : 0).FirstOrDefault();
            if (target is not null && location?.SourceTree?.FilePath is { } path)
            {
                target.Add($"{path}:{location.GetLineSpan().StartLinePosition.Line + 1}");
            }
        }

        var result = new List<(string, List<string>)>();
        if (forms.Count > 0)
        {
            result.Add(("form", forms));
        }

        if (controls.Count > 0)
        {
            result.Add(("user control", controls));
        }

        return result;
    }

    private static bool DerivesFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
            {
                return true;
            }
        }

        return false;
    }

    private static int CountPackages(string packagesConfig)
    {
        try
        {
            return System.Xml.Linq.XDocument.Load(packagesConfig).Descendants().Count(e => e.Name.LocalName == "package");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return 0;
        }
    }

    private static IEnumerable<string> ScanFiles(string directory)
    {
        var scanned = 0;
        var pending = new Stack<string>([directory]);
        while (pending.Count > 0 && scanned < MaxFilesScanned)
        {
            var current = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(current))
            {
                scanned++;
                yield return file;
            }

            foreach (var child in Directory.EnumerateDirectories(current))
            {
                if (!SkippedDirectories.Contains(Path.GetFileName(child)))
                {
                    pending.Push(child);
                }
            }
        }
    }
}
