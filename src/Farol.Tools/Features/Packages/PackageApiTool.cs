using System.ComponentModel;
using Farol.Core;
using Farol.Core.Text;
using Farol.Engine.Packages;
using Farol.Engine.Workspaces;
using Farol.Tools.Infrastructure;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using NuGet.Frameworks;
using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace Farol.Tools.Features.Packages;

[McpServerToolType]
public sealed class PackageApiTool(WorkspaceManager workspaces, PackageFeeds feeds)
{
    private const int MaxSummaryLength = 240;

    [McpServerTool(Name = "dotnet_package_api", Title = "Public API of a NuGet package version", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description(
        "Public types and members of an exact NuGet package version, with signatures and XML documentation summaries read from the package's " +
        "assemblies: check an API here instead of guessing it, since APIs change between versions. Without a query, lists the namespaces and types. " +
        "Reads the local NuGet caches first and downloads from the solution's feeds when the version is missing (not with --offline).")]
    public Task<string> Run(
        [Description("Package ID, e.g. Newtonsoft.Json.")] string package,
        [Description("Exact version, e.g. 12.0.3. Omit for the version the workspace uses, or the newest one when it uses none.")] string? version = null,
        [Description("A type or member: a name (JsonConvert, SerializeObject), a dotted name (JsonConvert.SerializeObject), a prefix, camel-case " +
            "humps (SerObj) or a documentation comment ID. A type returns its members. Omit to list namespaces and types.")] string? query = null,
        [Description("The target framework whose assemblies to read, e.g. net48 or net8.0. Default: the framework of the project that uses the package, " +
            "otherwise the newest .NET one in the package.")] string? targetFramework = null,
        [McpHeader(ToolParameters.WorkspaceHeader), Description(ToolParameters.WorkspaceDescription)] string? workspace = null,
        [Description(ToolParameters.MaxTokensDescription)] int maxTokens = TokenBudget.DefaultTokens,
        CancellationToken cancellationToken = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var id = package?.Trim() ?? string.Empty;
            if (id.Length == 0 || id.Any(char.IsWhiteSpace))
            {
                throw new FarolException(ErrorCodes.InvalidArgument, $"'{package}' is not a package ID.", "Pass the ID as nuget.org shows it, e.g. Newtonsoft.Json.");
            }

            var framework = ParseFramework(targetFramework);
            var session = workspaces.GetSession(workspace);
            var context = NuGetContext.Load(session.Target.Directory);
            var (resolved, note, usedFramework) = await ResolveVersionAsync(session, context, id, version, cancellationToken);
            framework ??= usedFramework;

            var identity = new PackageIdentity(id, resolved);
            var (folder, origin) = await LocateAsync(context, session.Target.Directory, identity, cancellationToken);
            var assemblies = PackageApi.SelectAssemblies(folder, framework);
            if (assemblies is null)
            {
                var available = PackageApi.SelectAssemblies(folder, target: null)?.Available;
                throw new FarolException(
                    ErrorCodes.InvalidArgument,
                    $"{id} {resolved.ToNormalizedString()} has no assemblies{(framework is null ? string.Empty : $" for {framework.GetShortFolderName()}")}.",
                    available is null
                        ? "It ships no lib/ or ref/ assemblies (a meta-package, tool or analyzer package): its dependencies carry the API."
                        : $"It has assemblies for: {string.Join(", ", available.Select(f => f.GetShortFolderName()))}. Pass one as targetFramework.");
            }

            var api = PackageApi.Open(assemblies);
            var text = new ResponseBuilder(maxTokens);
            var others = assemblies.Available.Where(f => !f.Equals(assemblies.Framework)).Select(f => f.GetShortFolderName()).ToList();
            text.Line($"{id} {resolved.ToNormalizedString()} · {assemblies.Folder}{(others.Count > 0 ? $" (also: {string.Join(", ", others)})" : string.Empty)} · {origin}");
            if (note is not null)
            {
                text.Line($"version: {note}");
            }

            if (string.IsNullOrWhiteSpace(query))
            {
                Overview(text, api);
            }
            else
            {
                Query(text, api, id, resolved, query, cancellationToken);
            }

            return text.ToString();
        });

    private async Task<(NuGetVersion Version, string? Note, NuGetFramework? Framework)> ResolveVersionAsync(
        WorkspaceSession session, NuGetContext context, string id, string? version, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(version))
        {
            if (!NuGetVersion.TryParse(version.Trim(), out var exact))
            {
                throw new FarolException(ErrorCodes.InvalidArgument, $"'{version}' is not a package version.", "Pass an exact version such as 12.0.3; ranges are not accepted.");
            }

            // The framework of a project that uses this version, if the workspace is already loaded: never wait for a load just for that.
            var loaded = session.CurrentSnapshot;
            var users = loaded is null ? [] : Usages(loaded, context, id).Where(u => u.Package.Version == exact).ToList();
            return (exact, null, users.Count == 0 ? null : FrameworkOf(users[0].Project, users[0].Package));
        }

        var snapshot = await session.GetSnapshotAsync(wait: true, cancellationToken);
        var usages = Usages(snapshot, context, id);
        if (usages.Count > 0)
        {
            var newest = usages.MaxBy(u => u.Package.Version);
            var projects = usages.Where(u => u.Package.Version == newest.Package.Version).Select(u => u.Project.Name).Distinct(StringComparer.OrdinalIgnoreCase);
            var others = usages.Select(u => u.Package.Version).Distinct().Where(v => v != newest.Package.Version).Select(v => v.ToNormalizedString()).ToList();
            var note = $"the one {string.Join(", ", projects)} resolve(s){(others.Count > 0 ? $"; the workspace also uses {string.Join(", ", others)}" : string.Empty)}";
            return (newest.Package.Version, note, FrameworkOf(newest.Project, newest.Package));
        }

        if (feeds.Offline)
        {
            throw new FarolException(ErrorCodes.InvalidArgument, $"The workspace does not use {id}, so there is no version to default to.", "Pass version: with --offline Farol cannot ask the feeds for the newest one.");
        }

        var (latest, problems) = await feeds.LatestAsync(context, id, cancellationToken);
        if (latest is null)
        {
            throw new FarolException(
                ErrorCodes.SymbolNotFound,
                $"No package {id} was found on the feeds ({string.Join(", ", context.Sources.Select(s => s.Name))}).{Problems(problems)}",
                "Check the ID; dotnet_packages lists the packages the workspace uses.");
        }

        return (latest, "the newest on the feeds; the workspace does not use this package", null);
    }

    private async Task<(string Folder, string Origin)> LocateAsync(NuGetContext context, string workspaceDirectory, PackageIdentity identity, CancellationToken cancellationToken)
    {
        if (context.FindInstalled(identity.Id, identity.Version) is { } installed)
        {
            return (installed, "local NuGet cache");
        }

        if (context.FindInRepository(workspaceDirectory, identity) is { } repository)
        {
            return (repository, "packages folder (packages.config)");
        }

        var name = $"{identity.Id} {identity.Version.ToNormalizedString()}";
        if (feeds.Offline)
        {
            throw new FarolException(
                ErrorCodes.PackageNotAvailable,
                $"{name} is not in the local NuGet caches, and Farol runs with --offline, so it cannot download it.",
                "Restore a project that uses this version, or start Farol without --offline. Farol does not guess an API it cannot read.");
        }

        var (folder, source, problems) = await feeds.DownloadAsync(context, identity, cancellationToken);
        return folder is not null && source is not null
            ? (folder, $"downloaded from {source} into the local NuGet cache")
            : throw new FarolException(
                ErrorCodes.PackageNotAvailable,
                $"{name} is neither in the local NuGet caches nor on the feeds ({string.Join(", ", context.SourcesFor(identity.Id).Select(s => s.Name))}).{Problems(problems)}",
                "Check the version: dotnet_packages lists the versions the workspace uses, and omitting version picks one.");
    }

    private static List<(ProjectPackages Project, InstalledPackage Package)> Usages(WorkspaceSnapshot snapshot, NuGetContext context, string id) =>
        [.. PackageInventory.Read(snapshot, context, projectName: null)
            .SelectMany(p => p.Packages.Where(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).Select(x => (p, x)))];

    private static NuGetFramework? FrameworkOf(ProjectPackages project, InstalledPackage package)
    {
        var name = package.Frameworks.Count > 0 ? package.Frameworks[0] : project.Frameworks.Count > 0 ? project.Frameworks[0] : null;
        return name is null ? null : NuGetFramework.Parse(name) is { IsUnsupported: false } parsed ? parsed : null;
    }

    private static NuGetFramework? ParseFramework(string? targetFramework)
    {
        if (string.IsNullOrWhiteSpace(targetFramework))
        {
            return null;
        }

        var framework = NuGetFramework.Parse(targetFramework.Trim());
        return framework.IsUnsupported || framework.IsAny
            ? throw new FarolException(ErrorCodes.InvalidArgument, $"'{targetFramework}' is not a target framework.", "Use a short name such as net48, netstandard2.0 or net8.0.")
            : framework;
    }

    private static void Overview(ResponseBuilder text, PackageApi api)
    {
        var types = api.PublicTypes();
        var namespaces = types.GroupBy(t => t.ContainingNamespace.IsGlobalNamespace ? "(global)" : t.ContainingNamespace.ToDisplayString(), StringComparer.Ordinal).ToList();
        text.Line($"public types: {types.Count} in {namespaces.Count} namespace(s). Pass query with a type or member for signatures and documentation.");
        for (var index = 0; index < namespaces.Count; index++)
        {
            var group = namespaces[index];
            var names = group.Select(t => t.ToDisplayString(PackageApi.TypeNameFormat)).ToList();
            var line = $"- {group.Key} ({names.Count}): {string.Join(", ", names)}";
            if (line.Length > 900)
            {
                var kept = new List<string>();
                foreach (var name in names)
                {
                    if (kept.Sum(k => k.Length + 2) + name.Length > 800)
                    {
                        break;
                    }

                    kept.Add(name);
                }

                line = $"- {group.Key} ({names.Count}): {string.Join(", ", kept)}, … {names.Count - kept.Count} more (query a name to see them)";
            }

            if (!text.TryLine(line))
            {
                text.More(namespaces.Count - index, "raise maxTokens, or query a type or member");
                return;
            }
        }
    }

    private static void Query(ResponseBuilder text, PackageApi api, string id, NuGetVersion version, string query, CancellationToken cancellationToken)
    {
        var matches = api.Find(query);
        if (matches.Count == 0)
        {
            throw new FarolException(
                ErrorCodes.SymbolNotFound,
                $"No public type or member of {id} {version.ToNormalizedString()} ({api.Assemblies.Folder}) matches '{query}'.",
                "Call without query to list its types. APIs differ between versions and target frameworks, so it may not exist in this one.");
        }

        var types = matches.OfType<INamedTypeSymbol>().ToList();
        if (types.Count == 1 && (matches.Count == 1 || types[0].Name.Equals(query.Trim().Split('.')[^1], StringComparison.Ordinal)))
        {
            TypeView(text, types[0], cancellationToken);
            return;
        }

        text.Line($"'{query}' matches {matches.Count} public type(s) or member(s):");
        for (var index = 0; index < matches.Count; index++)
        {
            if (!text.TryLine("- " + Line(PackageApi.Describe(matches[index], withContainingType: true, cancellationToken))))
            {
                text.More(matches.Count - index, "use a more specific query or raise maxTokens");
                return;
            }
        }
    }

    private static void TypeView(ResponseBuilder text, INamedTypeSymbol type, CancellationToken cancellationToken)
    {
        var entry = PackageApi.Describe(type, withContainingType: true, cancellationToken);
        text.Line($"{entry.Kind} {type.ToDisplayString()}{(type.IsStatic ? " · static" : type.IsAbstract && type.TypeKind == TypeKind.Class ? " · abstract" : type.IsSealed && type.TypeKind == TypeKind.Class ? " · sealed" : string.Empty)}{(entry.Obsolete ? " · obsolete" : string.Empty)}");
        text.Line($"summary: {entry.Summary ?? "(no XML documentation)"}");
        var bases = new[] { type.BaseType }.OfType<INamedTypeSymbol>().Where(b => b.SpecialType is not (SpecialType.System_Object or SpecialType.System_ValueType or SpecialType.System_Enum)).Concat(type.Interfaces).ToList();
        if (bases.Count > 0)
        {
            text.Line($"inherits: {string.Join(", ", bases.Select(b => b.ToDisplayString(Engine.Navigation.SymbolFormatter.Signature)))}");
        }

        var members = PackageApi.Members(type).Select(m => PackageApi.Describe(m, withContainingType: false, cancellationToken)).ToList();
        var nested = type.GetTypeMembers().Where(t => t.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected).ToList();
        text.Line($"members ({members.Count}){(nested.Count > 0 ? $" · nested types: {string.Join(", ", nested.Select(n => n.Name))}" : string.Empty)}:");
        for (var index = 0; index < members.Count; index++)
        {
            if (!text.TryLine("- " + Line(members[index])))
            {
                text.More(members.Count - index, "query Type.Member for the rest, or raise maxTokens");
                return;
            }
        }
    }

    private static string Line(ApiEntry entry)
    {
        var summary = entry.Summary is { } s ? $" — {(s.Length > MaxSummaryLength ? s[..MaxSummaryLength] + "…" : s)}" : string.Empty;
        return $"{entry.Kind} {entry.Signature}{(entry.Obsolete ? " · obsolete" : string.Empty)}{summary}";
    }

    private static string Problems(IReadOnlyList<string> problems) =>
        problems.Count == 0 ? string.Empty : $" Feed problems: {string.Join("; ", problems)}.";
}
