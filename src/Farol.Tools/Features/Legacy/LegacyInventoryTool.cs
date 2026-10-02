using System.ComponentModel;
using Farol.Core;
using Farol.Core.Paths;
using Farol.Core.Text;
using Farol.Engine.Legacy;
using Farol.Engine.Workspaces;
using Farol.Tools.Infrastructure;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Farol.Tools.Features.Legacy;

[McpServerToolType]
public sealed class LegacyInventoryTool(WorkspaceManager workspaces)
{
    private const int MaxHotspots = 5;

    [McpServerTool(Name = "dotnet_legacy_inventory", Title = "Inventory legacy .NET Framework technologies", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "What is legacy in a .NET solution and where, with counts: classic (non-SDK) projects, .NET Framework targets, packages.config; WebForms, " +
        "MVC 5, ASMX, WCF, WinForms and WPF with their pages, services, forms and XAML; web.config/app.config settings and binding redirects; and code " +
        "uses of legacy APIs (BinaryFormatter, System.Configuration, System.Web, Remoting…) with their lines and the files where they concentrate. " +
        "Start a modernization here; dotnet_portability then lists what breaks on modern .NET.")]
    public Task<string> Run(
        [Description("A project name as dotnet_overview lists it. Omit for every project.")] string? project = null,
        [McpHeader(ToolParameters.WorkspaceHeader), Description(ToolParameters.WorkspaceDescription)] string? workspace = null,
        [Description(ToolParameters.MaxTokensDescription)] int maxTokens = TokenBudget.DefaultTokens,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var session = workspaces.GetSession(workspace);
            var snapshot = await session.GetSnapshotAsync(wait: true, cancellationToken);
            if (project is not null && snapshot.Projects.FindByName(project).Count == 0)
            {
                throw new FarolException(ErrorCodes.InvalidArgument, $"No project named '{project}'.", $"Projects: {string.Join(", ", snapshot.Projects.Names.Take(40))}.");
            }

            new ToolProgress(progress).Report($"scanning {(project ?? session.Target.DisplayName)} for legacy technologies");
            var inventory = await LegacyInventory.BuildAsync(snapshot, session.Target, session.Report, project, cancellationToken);
            return Render(inventory, session.Target.DisplayName, workspaces.RootDirectory, maxTokens);
        });

    internal static string Render(LegacyInventoryReport inventory, string workspace, string root, int maxTokens)
    {
        var text = new ResponseBuilder(maxTokens);
        var projects = inventory.Projects.Select(p => p.Overview).ToList();
        var targets = projects.SelectMany(p => p.TargetFrameworks).GroupBy(t => t, StringComparer.OrdinalIgnoreCase).Select(g => $"{g.Key} ({g.Count()})");
        var packagesConfig = inventory.Projects.Where(p => p.PackagesConfigPackages > 0).Select(p => $"{p.Overview.Name} ({p.PackagesConfigPackages} packages)").ToList();
        var vb = projects.Where(p => p.Language == "VB").Select(p => p.Name).ToList();
        text.Line(
            $"legacy inventory: {workspace} · {projects.Count} project(s): {projects.Count(p => !p.IsSdkStyle)} classic (non-SDK), {projects.Count(p => p.IsSdkStyle)} SDK-style · " +
            $"targets: {string.Join(", ", targets)}{(packagesConfig.Count > 0 ? $" · packages.config: {string.Join(", ", packagesConfig)}" : string.Empty)}{(vb.Count > 0 ? $" · VB.NET: {string.Join(", ", vb)}" : string.Empty)}");

        var technologies = inventory.Items
            .GroupBy(i => (i.Technology, i.Area))
            .OrderBy(g => LegacyText.AreaOrder(g.Key.Area))
            .ThenBy(g => g.Key.Technology, StringComparer.Ordinal)
            .ToList();
        if (technologies.Count == 0)
        {
            text.Line("no legacy technology found.");
            return text.ToString();
        }

        text.Line($"technologies ({technologies.Count}):");
        for (var index = 0; index < technologies.Count; index++)
        {
            var group = technologies[index];
            var perProject = group.OrderBy(i => i.Project, StringComparer.OrdinalIgnoreCase)
                .Select(i => $"{i.Project}: {string.Join(", ", i.Counts)} ({LegacyText.Places(i.Locations, root)})");
            if (!text.TryLine($"- {group.Key.Technology} · {string.Join(" · ", perProject)}"))
            {
                text.More(technologies.Count - index, "pass project, or raise maxTokens");
                return text.ToString();
            }
        }

        var hotspots = inventory.Projects.SelectMany(p => p.Hotspots)
            .OrderByDescending(h => h.Uses)
            .ThenBy(h => h.FilePath, StringComparer.OrdinalIgnoreCase)
            .Take(MaxHotspots)
            .ToList();
        if (hotspots.Count > 0 && text.TryLine("hotspots (legacy API uses per file):"))
        {
            foreach (var hotspot in hotspots)
            {
                if (!text.TryLine($"- {DisplayPath.From(root, hotspot.FilePath)}: {hotspot.Uses} line(s) · {string.Join(", ", hotspot.Technologies)}"))
                {
                    break;
                }
            }
        }

        text.TryLine("next: dotnet_portability lists the APIs that break on the target framework; dotnet_migration_plan orders the work.");
        return text.ToString();
    }
}
