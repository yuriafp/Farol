using System.ComponentModel;
using Farol.Core;
using Farol.Core.Text;
using Farol.Engine.Packages;
using Farol.Engine.Workspaces;
using Farol.Tools.Infrastructure;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NuGet.Frameworks;
using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace Farol.Tools.Features.Packages;

[McpServerToolType]
public sealed class PackageApiTool(WorkspaceManager workspaces, PackageFeeds feeds)
{
    private const int MaxSummaryLength = 240;

    [McpServerTool(Name = "dotnet_package_api", Title = "Public API of a NuGet package version", ReadOnly = true, Idempotent = true, OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(PackageApiOutput))]
    [Description(
        "Public types and members of an exact NuGet package version, with signatures and XML documentation summaries read from the package's " +
        "assemblies: check an API here instead of guessing it, since APIs change between versions. Without a query, lists the namespaces and types. " +
        "Reads the local NuGet caches first and downloads from the solution's feeds when the version is missing (not with --offline).")]
    public Task<CallToolResult> Run(
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

            var data = new PackageApiOutput(id, resolved.ToNormalizedString(), assemblies.Framework.GetShortFolderName(), assemblies.Folder, others, origin, note, null, null, null, null);
            data = string.IsNullOrWhiteSpace(query)
                ? Overview(text, api, data)
                : Query(text, api, id, resolved, query, data, cancellationToken);
            return ToolOutput.Of(text.ToString(), data);
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

    private static PackageApiOutput Overview(ResponseBuilder text, PackageApi api, PackageApiOutput data)
    {
        var shown = new List<ApiNamespaceItem>();
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
                break;
            }

            shown.Add(new ApiNamespaceItem(group.Key, names));
        }

        return data with { PublicTypes = types.Count, Namespaces = new Page<ApiNamespaceItem>(namespaces.Count, 0, shown.Count < namespaces.Count ? shown.Count : null, shown) };
    }

    private static PackageApiOutput Query(ResponseBuilder text, PackageApi api, string id, NuGetVersion version, string query, PackageApiOutput data, CancellationToken cancellationToken)
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
            return data with { Type = TypeView(text, types[0], cancellationToken) };
        }

        text.Line($"'{query}' matches {matches.Count} public type(s) or member(s):");
        var shown = new List<ApiMemberItem>();
        for (var index = 0; index < matches.Count; index++)
        {
            var entry = PackageApi.Describe(matches[index], withContainingType: true, cancellationToken);
            if (!text.TryLine("- " + Line(entry)))
            {
                text.More(matches.Count - index, "use a more specific query or raise maxTokens");
                break;
            }

            shown.Add(Item(entry));
        }

        return data with { Matches = new Page<ApiMemberItem>(matches.Count, 0, shown.Count < matches.Count ? shown.Count : null, shown) };
    }

    private static ApiTypeItem TypeView(ResponseBuilder text, INamedTypeSymbol type, CancellationToken cancellationToken)
    {
        var entry = PackageApi.Describe(type, withContainingType: true, cancellationToken);
        text.Line($"{entry.Kind} {type.ToDisplayString()}{(type.IsStatic ? " · static" : type.IsAbstract && type.TypeKind == TypeKind.Class ? " · abstract" : type.IsSealed && type.TypeKind == TypeKind.Class ? " · sealed" : string.Empty)}{(entry.Obsolete ? " · obsolete" : string.Empty)}");
        text.Line($"summary: {entry.Summary ?? "(no XML documentation)"}");
        var bases = new[] { type.BaseType }.OfType<INamedTypeSymbol>().Where(b => b.SpecialType is not (SpecialType.System_Object or SpecialType.System_ValueType or SpecialType.System_Enum)).Concat(type.Interfaces).ToList();
        var inherits = bases.Select(b => b.ToDisplayString(Engine.Navigation.SymbolFormatter.Signature)).ToList();
        if (bases.Count > 0)
        {
            text.Line($"inherits: {string.Join(", ", inherits)}");
        }

        var members = PackageApi.Members(type).Select(m => PackageApi.Describe(m, withContainingType: false, cancellationToken)).ToList();
        var nested = type.GetTypeMembers().Where(t => t.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected).ToList();
        text.Line($"members ({members.Count}){(nested.Count > 0 ? $" · nested types: {string.Join(", ", nested.Select(n => n.Name))}" : string.Empty)}:");
        var shown = new List<ApiMemberItem>();
        for (var index = 0; index < members.Count; index++)
        {
            if (!text.TryLine("- " + Line(members[index])))
            {
                text.More(members.Count - index, "query Type.Member for the rest, or raise maxTokens");
                break;
            }

            shown.Add(Item(members[index]));
        }

        var modifiers = type.IsStatic ? "static" : type.IsAbstract && type.TypeKind == TypeKind.Class ? "abstract" : type.IsSealed && type.TypeKind == TypeKind.Class ? "sealed" : null;
        return new ApiTypeItem(
            entry.Kind, type.ToDisplayString(), modifiers, entry.Obsolete, entry.Summary, inherits, [.. nested.Select(n => n.Name)],
            new Page<ApiMemberItem>(members.Count, 0, shown.Count < members.Count ? shown.Count : null, shown));
    }

    private static ApiMemberItem Item(ApiEntry entry) => new(entry.Kind, entry.Signature, entry.Obsolete, entry.Summary, entry.Symbol.GetDocumentationCommentId());

    private static string Line(ApiEntry entry)
    {
        var summary = entry.Summary is { } s ? $" — {(s.Length > MaxSummaryLength ? s[..MaxSummaryLength] + "…" : s)}" : string.Empty;
        return $"{entry.Kind} {entry.Signature}{(entry.Obsolete ? " · obsolete" : string.Empty)}{summary}";
    }

    private static string Problems(IReadOnlyList<string> problems) =>
        problems.Count == 0 ? string.Empty : $" Feed problems: {string.Join("; ", problems)}.";
}

/// <summary>
/// Structured output of dotnet_package_api: <see cref="Namespaces"/> without a query, <see cref="Type"/> when the query names one type,
/// <see cref="Matches"/> otherwise.
/// </summary>
public sealed record PackageApiOutput(
    string Package,
    string Version,
    string TargetFramework,
    string Folder,
    IReadOnlyList<string> OtherTargetFrameworks,
    string Origin,
    string? VersionNote,
    int? PublicTypes,
    Page<ApiNamespaceItem>? Namespaces,
    ApiTypeItem? Type,
    Page<ApiMemberItem>? Matches);

public sealed record ApiNamespaceItem(string Namespace, IReadOnlyList<string> Types);

public sealed record ApiTypeItem(
    string Kind, string Name, string? Modifier, bool Obsolete, string? Summary, IReadOnlyList<string> Inherits, IReadOnlyList<string> NestedTypes, Page<ApiMemberItem> Members);

public sealed record ApiMemberItem(string Kind, string Signature, bool Obsolete, string? Summary, string? Id);
