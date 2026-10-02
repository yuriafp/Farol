using System.ComponentModel;
using Farol.Core;
using Farol.Core.Text;
using Farol.Engine.Legacy;
using Farol.Engine.Packages;
using Farol.Engine.Workspaces;
using Farol.Tools.Infrastructure;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Farol.Tools.Features.Legacy;

[McpServerToolType]
public sealed class PortabilityTool(WorkspaceManager workspaces)
{

    [McpServerTool(Name = "dotnet_portability", Title = "Find .NET Framework APIs that break on modern .NET", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Looks up every .NET Framework API the code uses in the target framework's reference assemblies and reports the ones missing or obsolete " +
        "there, grouped by technology (WebForms, WCF, BinaryFormatter, System.Configuration…), with their file:line uses and the replacement to " +
        "move to. Projects using WinForms or WPF are checked against the -windows target, where those run. Exact, not a list of known APIs: it reads the target's real API surface.")]
    public Task<string> Run(
        [Description("The migration target: net10.0 (default), net8.0, net10.0-windows, netstandard2.0…")] string targetFramework = "net10.0",
        [Description("A project name as dotnet_overview lists it. Omit for every project.")] string? project = null,
        [McpHeader(ToolParameters.WorkspaceHeader), Description(ToolParameters.WorkspaceDescription)] string? workspace = null,
        [Description(ToolParameters.MaxTokensDescription)] int maxTokens = TokenBudget.DefaultTokens,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var target = TargetSurface.ParseTarget(targetFramework);
            var session = workspaces.GetSession(workspace);
            var snapshot = await session.GetSnapshotAsync(wait: true, cancellationToken);
            if (project is not null && snapshot.Projects.FindByName(project).Count == 0)
            {
                throw new FarolException(ErrorCodes.InvalidArgument, $"No project named '{project}'.", $"Projects: {string.Join(", ", snapshot.Projects.Names.Take(40))}.");
            }

            var packages = NuGetContext.Load(session.Target.Directory).GlobalPackagesFolder;
            var report = await PortabilityAnalyzer.AnalyzeAsync(snapshot, target, packages, project, new ToolProgress(progress), cancellationToken);
            return Render(report, session.Target.DisplayName, workspaces.RootDirectory, maxTokens);
        });

    internal static string Render(PortabilityReport report, string workspace, string root, int maxTokens)
    {
        var text = new ResponseBuilder(maxTokens);
        var checkedProjects = report.Projects.Where(p => p.Skipped is null).ToList();
        var places = report.Issues.SelectMany(i => i.Api.Locations).Distinct().Count();
        text.Line(
            $"portability: {workspace} → {report.Target} · {checkedProjects.Count} .NET Framework project(s) checked · " +
            $"{report.Issues.Count} API(s) missing or obsolete, used in {places} place(s), in {report.Issues.Select(i => i.Project).Distinct(StringComparer.OrdinalIgnoreCase).Count()} project(s)");
        if (report.Surfaces.Count > 0)
        {
            text.Line($"target API from: {string.Join(", ", report.Surfaces)}");
        }

        text.Line($"projects: {string.Join(" · ", report.Projects.Select(Describe))}");
        if (report.Issues.Count == 0)
        {
            text.Line(checkedProjects.Count == 0 ? "nothing to check: no project targets .NET Framework." : $"every .NET Framework API the code uses exists on {report.Target}.");
            return text.ToString();
        }

        var groups = report.Issues
            .GroupBy(i => i.Technology?.Name ?? $"Other ({i.Api.Namespace})")
            .OrderBy(g => LegacyText.AreaOrder(g.First().Technology?.Area ?? "Other"))
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
        var shown = 0;
        foreach (var group in groups)
        {
            var technology = group.First().Technology;
            var projects = string.Join(", ", group.Select(i => i.Project).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase));
            var replacement = technology is null ? string.Empty : $" → {technology.Replacement}";
            if (!text.TryLine($"{group.Key} ({projects}){replacement}"))
            {
                text.More(report.Issues.Count - shown, "pass project, or raise maxTokens");
                return text.ToString();
            }

            var messages = new HashSet<string>(StringComparer.Ordinal);
            foreach (var issue in group.OrderBy(i => i.Api.Locations[0].FilePath, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Api.Locations[0].Line).ThenBy(i => i.Api.Display, StringComparer.Ordinal))
            {
                var status = issue.Availability.Status == ApiStatus.Missing
                    ? "missing"
                    : $"obsolete{(issue.Availability.DiagnosticId is { } id ? $" ({id})" : string.Empty)}{(issue.Availability.Message is { } message && messages.Add(message) ? $": {message}" : string.Empty)}";
                if (!text.TryLine($"- {issue.Api.Display} · {status} · {LegacyText.Places(issue.Api.Locations, root)}"))
                {
                    text.More(report.Issues.Count - shown, "pass project, or raise maxTokens");
                    return text.ToString();
                }

                shown++;
            }
        }

        return text.ToString();
    }

    private static string Describe(ProjectPortability project) =>
        project.Skipped is { } reason ? $"{project.Name} (skipped: {reason})" : $"{project.Name} ({project.From} → {project.To}, {project.ApisChecked} APIs)";
}
