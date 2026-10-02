using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Farol.Core.Files;
using Farol.Core.Paths;

namespace Farol.Engine.Legacy;

/// <summary>A document migration_plan wrote, relative to the root, and whether it existed before.</summary>
public sealed record WrittenDocument(string Path, bool Created);

/// <summary>
/// The migration plan as the three Markdown documents a team keeps in the repository: assessment.md (what is legacy
/// and what does not port), plan.md (the ordered steps) and tasks.md (a checklist). Regenerating keeps the boxes
/// already ticked in tasks.md.
/// </summary>
public static partial class MigrationDocuments
{
    public const string Folder = "docs/modernization";

    /// <summary>
    /// Where the documents go: docs/modernization under the repository root (the nearest folder with .git) when Farol
    /// trusts it, otherwise under the workspace directory.
    /// </summary>
    public static string DirectoryFor(string workspaceDirectory, PathSandbox paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        for (var directory = new DirectoryInfo(workspaceDirectory); directory is not null; directory = directory.Parent)
        {
            var git = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
            {
                if (paths.Contains(directory.FullName))
                {
                    return Path.Combine(directory.FullName, "docs", "modernization");
                }

                break;
            }
        }

        return Path.Combine(workspaceDirectory, "docs", "modernization");
    }

    public static async Task<IReadOnlyList<WrittenDocument>> WriteAsync(MigrationPlan plan, string directory, PathSandbox paths, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(paths);
        var root = paths.Root;
        var tasksPath = Path.Combine(directory, "tasks.md");
        IReadOnlySet<string> ticked = File.Exists(tasksPath) ? Ticked(await File.ReadAllTextAsync(tasksPath, cancellationToken)) : new HashSet<string>(StringComparer.Ordinal);
        var documents = new[]
        {
            (Name: "assessment.md", Content: Assessment(plan, root, now)),
            (Name: "plan.md", Content: Plan(plan, root, now)),
            (Name: "tasks.md", Content: Tasks(plan, now, ticked)),
        };

        Directory.CreateDirectory(directory);
        var written = new List<WrittenDocument>();
        foreach (var (name, content) in documents)
        {
            var path = Path.Combine(directory, name);
            paths.Demand(path, $"The migration document {name}");
            var created = !File.Exists(path);
            await AtomicFile.WriteAllTextAsync(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
            written.Add(new WrittenDocument(DisplayPath.From(root, path), created));
        }

        return written;
    }

    public static string Assessment(MigrationPlan plan, string root, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var text = new StringBuilder();
        Header(text, now, "Regenerating replaces this file.");
        text.AppendLine(CultureInfo.InvariantCulture, $"# Modernization assessment — {plan.Workspace} → {plan.Target}").AppendLine();

        var projects = plan.Inventory.Projects;
        text.AppendLine("## Projects").AppendLine();
        text.AppendLine("| Project | Language | Format | Target frameworks | Kind | App models | Source files |");
        text.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var project in projects)
        {
            var o = project.Overview;
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {o.Name} | {o.Language} | {(o.IsSdkStyle ? "SDK-style" : "classic")}{(o.HasPackagesConfig ? ", packages.config" : string.Empty)} | {string.Join(", ", o.TargetFrameworks)} | {MigrationPlanner.KindOf(o)} | {Cell(o.AppModels)} | {project.SourceFiles} |");
        }

        text.AppendLine().AppendLine("## Legacy technologies").AppendLine();
        if (plan.Inventory.Items.Count == 0)
        {
            text.AppendLine("None found.");
        }
        else
        {
            text.AppendLine("| Technology | Project | Amount | Where |");
            text.AppendLine("|---|---|---|---|");
            foreach (var item in plan.Inventory.Items.OrderBy(i => LegacyCatalog.AreaOrder(i.Area)).ThenBy(i => i.Technology, StringComparer.Ordinal).ThenBy(i => i.Project, StringComparer.OrdinalIgnoreCase))
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"| {item.Technology} | {item.Project} | {string.Join(", ", item.Counts)} | {DisplayPath.Places(root, item.Locations, 8)} |");
            }
        }

        text.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"## Portability to {plan.Portability.Target}").AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"Checked against the reference assemblies of {string.Join(", ", plan.Portability.Surfaces.DefaultIfEmpty("no targeting pack (no .NET Framework project)"))}.").AppendLine();
        if (plan.Portability.Issues.Count == 0)
        {
            text.AppendLine("Every .NET Framework API the code uses exists on the target.");
        }

        foreach (var group in plan.Portability.Issues.GroupBy(i => i.Technology?.Name ?? "Other").OrderBy(g => LegacyCatalog.AreaOrder(g.First().Technology?.Area ?? "Other")).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"### {group.Key}").AppendLine();
            if (group.First().Technology is { } technology)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"Replacement: {technology.Replacement}").AppendLine();
            }

            text.AppendLine("| API | Project | On the target | Where |");
            text.AppendLine("|---|---|---|---|");
            foreach (var issue in group.OrderBy(i => i.Project, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Api.Display, StringComparer.Ordinal))
            {
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"| `{issue.Api.Display}` | {issue.Project} | {Status(issue.Availability)} | {DisplayPath.Places(root, issue.Api.Locations.Select(l => $"{l.FilePath}:{l.Line}"), 8)} |");
            }

            text.AppendLine();
        }

        if (plan.Configs.Count > 0)
        {
            text.AppendLine("## Configuration").AppendLine();
            foreach (var config in plan.Configs)
            {
                var wcf = config.ServiceModel is { } model ? $", WCF: {model.Services.Count} service(s), {model.Clients.Count} client endpoint(s)" : string.Empty;
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"- {DisplayPath.From(root, config.FilePath)}: {config.AppSettings.Count} app setting(s), {config.ConnectionStrings.Count} connection string(s), {config.MaskedCount} secret(s){wcf}, {config.BindingRedirects.Count} binding redirect(s)");
            }
        }

        return text.ToString();
    }

    public static string Plan(MigrationPlan plan, string root, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var text = new StringBuilder();
        Header(text, now, "Regenerating replaces this file.");
        text.AppendLine(CultureInfo.InvariantCulture, $"# Migration plan — {plan.Workspace} → {plan.Target}").AppendLine();
        text.AppendLine("## Strategy").AppendLine();
        foreach (var line in plan.Strategy)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- {line}");
        }

        if (plan.AlreadyModern.Count > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- Already on modern .NET: {string.Join(", ", plan.AlreadyModern)}.");
        }

        text.AppendLine().AppendLine("## Steps").AppendLine();
        foreach (var step in plan.Steps)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"### {step.Number}. {step.Project} — {step.Kind}, {step.From} → {step.To} (size {step.Size})").AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"After: {(step.DependsOn.Count == 0 ? "nothing in the solution" : string.Join(", ", step.DependsOn))}. Project file: `{DisplayPath.From(root, step.FilePath)}`.").AppendLine();
            text.AppendLine(step.Approach).AppendLine();
            for (var index = 0; index < step.Tasks.Count; index++)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"{index + 1}. {step.Tasks[index]}");
            }

            text.AppendLine();
        }

        text.AppendLine("Sizes: S converts without code changes, M replaces APIs, L rewrites a UI or service layer.");
        return text.ToString();
    }

    public static string Tasks(MigrationPlan plan, DateTimeOffset now, IReadOnlySet<string> ticked)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(ticked);
        var text = new StringBuilder();
        Header(text, now, "Regenerating replaces this file but keeps the boxes you ticked.");
        text.AppendLine(CultureInfo.InvariantCulture, $"# Migration tasks — {plan.Workspace} → {plan.Target}").AppendLine();
        foreach (var step in plan.Steps)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"## {step.Number}. {step.Project}").AppendLine();
            foreach (var task in step.Tasks)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"- [{(ticked.Contains(Normalize(task)) ? "x" : " ")}] {task}");
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    /// <summary>The tasks ticked in an existing tasks.md, compared by text.</summary>
    public static IReadOnlySet<string> Ticked(string tasksMarkdown)
    {
        ArgumentNullException.ThrowIfNull(tasksMarkdown);
        return TickedLine().Matches(tasksMarkdown).Select(m => Normalize(m.Groups["task"].Value)).ToHashSet(StringComparer.Ordinal);
    }

    private static void Header(StringBuilder text, DateTimeOffset now, string note) =>
        text.AppendLine(CultureInfo.InvariantCulture, $"<!-- Generated by Farol (dotnet_migration_plan) on {now:yyyy-MM-dd}. {note} -->").AppendLine();

    private static string Status(ApiAvailability availability) => availability.Status switch
    {
        ApiStatus.Missing => "missing",
        _ => $"obsolete{(availability.DiagnosticId is { } id ? $" ({id})" : string.Empty)}",
    };

    private static string Cell(IReadOnlyList<string> values) => values.Count == 0 ? "—" : string.Join(", ", values);

    private static string Normalize(string task) => string.Join(' ', task.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

    [GeneratedRegex(@"^\s*- \[[xX]\] (?<task>.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex TickedLine();
}
