using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Farol.Engine.Navigation;
using Farol.Engine.Workspaces;
using Microsoft.CodeAnalysis;

namespace Farol.Engine.Markup;

/// <summary>
/// References from markup to code, keyed by symbol ID. Built lazily once per snapshot (markup edits produce a
/// new snapshot); parsing is cached per file, so only changed files are read again.
/// </summary>
public sealed class MarkupIndex
{
    private static readonly ConditionalWeakTable<WorkspaceSnapshot, Lazy<Task<MarkupIndex>>> Indexes = new();
    private static readonly ConcurrentDictionary<string, ParsedFile> ParseCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, List<MarkupReference>> _bySymbol;

    private MarkupIndex(Dictionary<string, List<MarkupReference>> bySymbol, int files)
    {
        _bySymbol = bySymbol;
        Files = files;
    }

    /// <summary>Markup files scanned for this snapshot.</summary>
    public int Files { get; }

    public IReadOnlyList<MarkupReference> ReferencesTo(string symbolId) =>
        _bySymbol.TryGetValue(symbolId, out var references) ? references : [];

    public static Task<MarkupIndex> GetAsync(WorkspaceSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        // Built once per snapshot and shared by concurrent requests, so a single caller's cancellation must not cancel it.
        return Indexes.GetValue(snapshot, s => new Lazy<Task<MarkupIndex>>(() => BuildAsync(s))).Value.WaitAsync(cancellationToken);
    }

    private static async Task<MarkupIndex> BuildAsync(WorkspaceSnapshot snapshot)
    {
        var bySymbol = new Dictionary<string, List<MarkupReference>>(StringComparer.Ordinal);
        var compilations = new Dictionary<ProjectId, Compilation?>();
        var files = snapshot.Projects.ProjectDirectories
            .SelectMany(MarkupFiles.Enumerate)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var file in files)
        {
            var references = Parse(file);
            var projects = snapshot.Projects.ProjectsContaining(file);
            if (references.Count == 0 || projects.Count == 0)
            {
                continue;
            }

            // Markup is not target-framework specific: resolve against the first variant of the owning project.
            var projectId = projects[0];
            if (!compilations.TryGetValue(projectId, out var compilation))
            {
                compilation = snapshot.Solution.GetProject(projectId) is { } project ? await project.GetCompilationAsync() : null;
                compilations[projectId] = compilation;
            }

            if (compilation is null)
            {
                continue;
            }

            var projectName = snapshot.Projects.Get(projectId)?.Name ?? string.Empty;
            foreach (var reference in references)
            {
                foreach (var symbol in Resolve(compilation, reference))
                {
                    var id = SymbolFormatter.Id(symbol);
                    if (!bySymbol.TryGetValue(id, out var list))
                    {
                        list = [];
                        bySymbol[id] = list;
                    }

                    list.Add(new MarkupReference(file, reference.Line, reference.Column, reference.Snippet, projectName, reference.Detail));
                }
            }
        }

        return new MarkupIndex(bySymbol, files.Count);
    }

    private static IReadOnlyList<RawMarkupReference> Parse(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (ParseCache.TryGetValue(path, out var cached) && cached.LastWriteUtc == info.LastWriteTimeUtc && cached.Length == info.Length)
            {
                return cached.References;
            }

            var content = File.ReadAllText(path);
            var references = MarkupFiles.IsXaml(path) ? XamlMarkupParser.Parse(content) : WebFormsMarkupParser.Parse(content);
            ParseCache[path] = new ParsedFile(info.LastWriteTimeUtc, info.Length, references);
            return references;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<ISymbol> Resolve(Compilation compilation, RawMarkupReference reference)
    {
        if (reference.Target == MarkupTarget.Type)
        {
            return compilation.GetTypeByMetadataName(reference.Name) is { } type ? [type] : [];
        }

        if (reference.ContainerType is null || compilation.GetTypeByMetadataName(reference.ContainerType) is not { } container)
        {
            return [];
        }

        return MembersNamed(container, reference.Name).Where(member => Matches(member, reference.Filter));
    }

    // Walks the container and its source base classes; stops at framework bases (Page, Window) whose members are noise here.
    private static IEnumerable<ISymbol> MembersNamed(INamedTypeSymbol type, string name)
    {
        for (var current = type; current is not null && current.Locations.Any(l => l.IsInSource); current = current.BaseType)
        {
            foreach (var member in current.GetMembers(name))
            {
                yield return member;
            }
        }
    }

    private static bool Matches(ISymbol member, MemberFilter filter) => filter switch
    {
        MemberFilter.Method => member is IMethodSymbol { MethodKind: MethodKind.Ordinary },
        MemberFilter.EventHandler => member is IMethodSymbol { MethodKind: MethodKind.Ordinary, Parameters.Length: 2 } method && IsEventArgs(method.Parameters[1].Type),
        MemberFilter.FieldOrProperty => member is IFieldSymbol or IPropertySymbol,
        _ => member is IMethodSymbol { MethodKind: MethodKind.Ordinary } or IPropertySymbol or IFieldSymbol or IEventSymbol,
    };

    private static bool IsEventArgs(ITypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.Name == "EventArgs" && current.ContainingNamespace?.ToDisplayString() == "System")
            {
                return true;
            }
        }

        return false;
    }

    private sealed record ParsedFile(DateTime LastWriteUtc, long Length, IReadOnlyList<RawMarkupReference> References);
}
