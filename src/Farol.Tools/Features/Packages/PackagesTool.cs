using System.ComponentModel;
using Farol.Core;
using Farol.Core.Text;
using Farol.Engine.Packages;
using Farol.Engine.Workspaces;
using Farol.Tools.Infrastructure;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Farol.Tools.Features.Packages;

[McpServerToolType]
public sealed class PackagesTool(WorkspaceManager workspaces, PackageFeeds feeds)
{
    [McpServerTool(Name = "dotnet_packages", Title = "List NuGet packages and their problems", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description(
        "NuGet packages of each project with the versions restore resolved: direct ones and transitive ones with the package that brings each in, " +
        "for PackageReference projects (central package management included) and packages.config ones. Flags vulnerable packages (with their " +
        "advisories), deprecated ones (with the replacement) and newer versions, from the solution's NuGet feeds. Use it before upgrading or " +
        "adding a package, or to answer which version a project really uses.")]
    public Task<string> Run(
        [Description("A project name as dotnet_overview lists it. Omit for every project.")] string? project = null,
        [Description("Which packages, comma-separated: all (default: direct and transitive), direct, vulnerable, deprecated, outdated. " +
            "Problem filters combine as 'any of' (vulnerable,deprecated); with direct they apply to direct packages only.")] string include = "all",
        [McpHeader(ToolParameters.WorkspaceHeader), Description(ToolParameters.WorkspaceDescription)] string? workspace = null,
        [Description(ToolParameters.MaxTokensDescription)] int maxTokens = TokenBudget.DefaultTokens,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var filter = PackageFilter.Parse(include);
            var session = workspaces.GetSession(workspace);
            var snapshot = await session.GetSnapshotAsync(wait: true, cancellationToken);
            if (project is not null && snapshot.Projects.FindByName(project).Count == 0)
            {
                throw new FarolException(ErrorCodes.InvalidArgument, $"No project named '{project}'.", $"Projects: {string.Join(", ", snapshot.Projects.Names.Take(40))}.");
            }

            var context = NuGetContext.Load(session.Target.Directory);
            var projects = PackageInventory.Read(snapshot, context, project);
            var identities = projects.SelectMany(p => p.Packages).Select(p => p.Identity).Distinct().ToList();
            if (!feeds.Offline && identities.Count > 0)
            {
                new ToolProgress(progress).Report($"checking {identities.Count} package version(s) against {string.Join(", ", context.Sources.Select(s => s.Name))}");
            }

            var check = await feeds.CheckAsync(context, identities, cancellationToken);
            var text = new ResponseBuilder(maxTokens);
            PackagesRenderer.Render(text, session.Target, projects, check, filter, feeds.Offline, workspaces.RootDirectory);
            return text.ToString();
        });
}

/// <summary>The include argument: an optional direct-only scope and problem filters that combine as "any of".</summary>
internal sealed record PackageFilter(bool DirectOnly, bool Vulnerable, bool Deprecated, bool Outdated)
{
    private static readonly string[] Options = ["all", "direct", "vulnerable", "deprecated", "outdated"];

    public bool AnyProblem => Vulnerable || Deprecated || Outdated;

    public bool IsAll => !DirectOnly && !AnyProblem;

    public static PackageFilter Parse(string include)
    {
        var parts = (include ?? "all").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p => p.ToUpperInvariant()).ToList();
        if (parts.FirstOrDefault(p => !Options.Contains(p, StringComparer.OrdinalIgnoreCase)) is { } unknown)
        {
            throw new FarolException(ErrorCodes.InvalidArgument, $"Unknown include option '{unknown.ToLowerInvariant()}'.", $"Use {string.Join(", ", Options)}.");
        }

        return new PackageFilter(parts.Contains("DIRECT"), parts.Contains("VULNERABLE"), parts.Contains("DEPRECATED"), parts.Contains("OUTDATED"));
    }

    public override string ToString() =>
        IsAll ? "all" : string.Join(',', new[] { DirectOnly ? "direct" : null, Vulnerable ? "vulnerable" : null, Deprecated ? "deprecated" : null, Outdated ? "outdated" : null }.OfType<string>());
}
