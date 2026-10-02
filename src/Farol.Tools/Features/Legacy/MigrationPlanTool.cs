using System.ComponentModel;
using Farol.Core;
using Farol.Core.Paths;
using Farol.Core.Text;
using Farol.Engine.Legacy;
using Farol.Engine.Packages;
using Farol.Engine.Workspaces;
using Farol.Tools.Infrastructure;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Farol.Tools.Features.Legacy;

[McpServerToolType]
public sealed partial class MigrationPlanTool(WorkspaceManager workspaces, ServerPermissions permissions, CallerContext caller, ILogger<MigrationPlanTool> logger)
{
    [McpServerTool(Name = "dotnet_migration_plan", Title = "Plan a .NET Framework to modern .NET migration", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "An ordered plan to move a .NET Framework solution to modern .NET, built from dotnet_legacy_inventory and dotnet_portability: one step per " +
        "project, bottom-up by project dependency, each with its approach (multi-target, retarget to -windows, or rebuild on ASP.NET Core), a size, " +
        "and concrete tasks citing file:line. write=true also writes assessment.md, plan.md and tasks.md to docs/modernization/ (ticked tasks " +
        "survive regeneration). It writes documents, never code.")]
    public Task<string> Run(
        [Description("The migration target: net10.0 (default), net8.0, netstandard2.0…")] string targetFramework = "net10.0",
        [Description("Also write docs/modernization/assessment.md, plan.md and tasks.md in the repository. Default: false (the plan is only returned).")] bool write = false,
        [McpHeader(ToolParameters.WorkspaceHeader), Description(ToolParameters.WorkspaceDescription)] string? workspace = null,
        [Description(ToolParameters.MaxTokensDescription)] int maxTokens = TokenBudget.DefaultTokens,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default) =>
        ToolGuard.RunAsync(async () =>
        {
            if (write)
            {
                permissions.Demand("write the migration plan", "Call without write=true to get the plan in the response.");
            }

            var target = TargetSurface.ParseTarget(targetFramework);
            var session = workspaces.GetSession(workspace);
            var snapshot = await session.GetSnapshotAsync(wait: true, cancellationToken);
            var reporter = new ToolProgress(progress);
            reporter.Report($"inventorying {session.Target.DisplayName}");
            var inventory = await LegacyInventory.BuildAsync(snapshot, session.Target, session.Report, projectName: null, cancellationToken);
            var packages = NuGetContext.Load(session.Target.Directory).GlobalPackagesFolder;
            var portability = await PortabilityAnalyzer.AnalyzeAsync(snapshot, target, packages, projectName: null, reporter, cancellationToken);
            var root = workspaces.RootDirectory;
            var plan = MigrationPlanner.Build(session.Target.DisplayName, inventory, portability, target, root);

            IReadOnlyList<WrittenDocument> written = [];
            if (write)
            {
                var directory = MigrationDocuments.DirectoryFor(session.Target.Directory, workspaces.Paths);
                LogWrite(logger, directory, caller.User, caller.Origin);
                written = await MigrationDocuments.WriteAsync(plan, directory, workspaces.Paths, DateTimeOffset.Now, cancellationToken);
            }

            return Render(plan, written, maxTokens);
        });

    internal static string Render(MigrationPlan plan, IReadOnlyList<WrittenDocument> written, int maxTokens)
    {
        var text = new ResponseBuilder(maxTokens);
        var rest = written.FirstOrDefault(w => w.Path.EndsWith("/plan.md", StringComparison.Ordinal)) is { } document
            ? $"the full plan is in {document.Path}"
            : "raise maxTokens, or call with write=true for the documents";
        var sizes = plan.Steps.GroupBy(s => s.Size).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Count()} {g.Key}");
        text.Line($"migration plan: {plan.Workspace} → {plan.Target} · {plan.Steps.Count} step(s) · sizes: {string.Join(", ", sizes)} (S converts, M replaces APIs, L rewrites a UI or service layer)");
        if (written.Count > 0)
        {
            text.Line($"wrote: {string.Join(", ", written.Select(w => $"{w.Path} ({(w.Created ? "created" : "updated")})"))}");
        }

        if (plan.AlreadyModern.Count > 0)
        {
            text.Line($"already on modern .NET: {string.Join(", ", plan.AlreadyModern)}");
        }

        text.Line("strategy:");
        foreach (var line in plan.Strategy)
        {
            text.Line($"- {line}");
        }

        for (var index = 0; index < plan.Steps.Count; index++)
        {
            var step = plan.Steps[index];
            var header = $"{step.Number}. {step.Project} — {step.Kind}, {step.From} → {step.To} · size {step.Size} · after: {(step.DependsOn.Count == 0 ? "nothing" : string.Join(", ", step.DependsOn))}";
            if (!text.TryLine(header))
            {
                text.More(plan.Steps.Count - index, rest);
                return text.ToString();
            }

            for (var task = 0; task < step.Tasks.Count; task++)
            {
                if (!text.TryLine($"   - {step.Tasks[task]}"))
                {
                    text.More(step.Tasks.Count - task + plan.Steps.Skip(index + 1).Sum(s => s.Tasks.Count), rest);
                    return text.ToString();
                }
            }
        }

        if (written.Count == 0)
        {
            text.TryLine($"write=true saves this plan with the full assessment to {MigrationDocuments.Folder}/ (assessment.md, plan.md, tasks.md).");
        }

        return text.ToString();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Migration plan written to {Directory} for {User} via {Origin}")]
    private static partial void LogWrite(ILogger logger, string directory, string user, string origin);
}
