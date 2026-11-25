using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Farol.Engine.Analysis;
using Farol.Engine.Navigation;
using Farol.Engine.Workspaces;
using Microsoft.Data.Sqlite;

namespace Farol.Engine.Indexing;

/// <summary>A declaration as the index stores it: everything navigation answers show, without a compilation.</summary>
public sealed record IndexedSymbol(
    long Key,
    string Id,
    string Name,
    string Kind,
    string Signature,
    string Member,
    string Namespace,
    IReadOnlyList<string> Projects,
    IReadOnlyList<string> TargetFrameworks,
    string? Summary,
    IReadOnlyList<string> Inherits,
    string? Via,
    string? FilePath,
    int Line,
    int StartLine,
    int EndLine,
    bool IsExternal,
    string? Assembly,
    string Language,
    int Depth,
    IReadOnlyList<string> Containers,
    string Match,
    bool IsAbstract,
    string TypeKind)
{
    /// <summary>As <see cref="SymbolFormatter.Display"/> writes it.</summary>
    public string Display => $"{Kind} {Signature}";
}

/// <summary>A reference or definition from the index, as <see cref="SourceHit"/>.</summary>
public sealed record IndexedHit(string FilePath, int Line, int Column, string Snippet, string Project, IReadOnlyList<string> TargetFrameworks, string Kind, string? Via = null);

public sealed record IndexedReferences(IReadOnlyList<IndexedHit> Definitions, IReadOnlyList<IndexedHit> References, IReadOnlyList<IndexedSymbol> Through);

public sealed record IndexedRelation(string Relation, IndexedSymbol Symbol, string Signature);

public sealed record IndexedCallNode(IndexedSymbol Symbol, IReadOnlyList<CallSite> Sites, IReadOnlyList<IndexedCallNode> Children);

public sealed record IndexedDeclaration(int Depth, IndexedSymbol Symbol, string FilePath, int Line);

/// <summary>
/// How current an index is: when it was written, the files whose time or size changed since (AC-51), and whether a
/// project file did, in which case the projects themselves may have changed.
/// </summary>
public sealed record IndexFreshness(DateTimeOffset WrittenAt, IReadOnlyList<string> ChangedFiles, bool ProjectsChanged)
{
    private readonly HashSet<string> _changed = new(ChangedFiles, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public bool IsChanged(string? path) => path is not null && _changed.Contains(path);
}

/// <summary>
/// One workspace's persistent index, read-only. Each query opens the file and closes it again, so a newer index can
/// replace it at any time. Answers mirror the navigation finders' (<see cref="DeclarationSearch"/>,
/// <see cref="ReferenceFinder"/>, <see cref="HierarchyFinder"/>, <see cref="CallHierarchyFinder"/>, <see cref="OutlineBuilder"/>).
/// </summary>
public sealed partial class WorkspaceIndex
{
    private const string SymbolColumns = """
        s.id, s.docid, s.name, s.kind, s.signature, s.member, s.namespace, s.projects, s.frameworks, s.summary, s.inherits, s.via,
        f.path, s.line, s.start_line, s.end_line, s.external, s.assembly, s.language, s.depth, s.containers, s.match, s.abstract, s.type_kind
        """;

    private const string FromSymbols = "FROM symbols s LEFT JOIN files f ON f.id = s.file";

    // Paths as the file system compares them.
    private static readonly string PathCollation = OperatingSystem.IsWindows() ? " COLLATE NOCASE" : string.Empty;

    private static readonly TimeSpan FreshnessLifetime = TimeSpan.FromSeconds(2);

    private readonly Lock _gate = new();
    private (DateTime At, IndexFreshness Value)? _freshness;

    // The file's time when it was read: a file written again since (by another server) holds other keys.
    private readonly long _fileTicks;

    internal WorkspaceIndex(string path, DateTimeOffset writtenAt, string loader)
    {
        FilePath = path;
        WrittenAt = writtenAt;
        Loader = loader;
        _fileTicks = File.GetLastWriteTimeUtc(path).Ticks;
    }

    public string FilePath { get; }

    public DateTimeOffset WrittenAt { get; }

    /// <summary>The loader of the load the index was written from.</summary>
    public string Loader { get; }

    /// <summary>Reads the index's meta table: null when it is not an index of this format (AC-52).</summary>
    internal static WorkspaceIndex? Read(string path)
    {
        using var connection = IndexSchema.Open(path, readOnly: true);
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT key, value FROM meta WHERE key IN ('format', 'written', 'loader')";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                meta[reader.GetString(0)] = reader.GetString(1);
            }
        }

        return meta.GetValueOrDefault("format") == IndexSchema.FormatVersion.ToString(CultureInfo.InvariantCulture)
            && DateTimeOffset.TryParse(meta.GetValueOrDefault("written"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var written)
                ? new WorkspaceIndex(path, written, meta.GetValueOrDefault("loader") ?? "msbuild-workspace")
                : null;
    }

    /// <summary>The files changed since the index was written, checked again at most every two seconds.</summary>
    public IndexFreshness Freshness()
    {
        lock (_gate)
        {
            if (_freshness is { } cached && DateTime.UtcNow - cached.At < FreshnessLifetime)
            {
                return cached.Value;
            }
        }

        // Read first, then the files: a connection held open while every file is looked at would stop a server that wrote
        // a new index from moving it into place.
        var files = new List<(string Path, int Kind, long Ticks, long Size)>();
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT path, kind, ticks, size FROM files";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                files.Add((reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetInt64(3)));
            }
        }

        var changed = new List<string>();
        var projects = false;
        foreach (var (path, kind, ticks, size) in files)
        {
            if (Unchanged(path, ticks, size))
            {
                continue;
            }

            if (kind == IndexSchema.ProjectFile)
            {
                projects = true;
            }
            else
            {
                changed.Add(path);
            }
        }

        changed.Sort(StringComparer.OrdinalIgnoreCase);
        var freshness = new IndexFreshness(WrittenAt, changed, projects);
        lock (_gate)
        {
            _freshness = (DateTime.UtcNow, freshness);
        }

        return freshness;
    }

    private static bool Unchanged(string path, long ticks, long size)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length == size && info.LastWriteTimeUtc.Ticks == ticks : size == -1;
        }
        catch (IOException)
        {
            // Deleted or moved while looked at.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public SolutionOverview? Overview(WorkspaceTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = 'overview'";
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<IndexedOverview>(json)?.For(target) : null;
    }

    /// <summary>As <see cref="DeclarationSearch.SearchAsync"/>: the same patterns, kinds and order.</summary>
    public IReadOnlyList<IndexedSymbol> FindSymbols(string query, string kind, string? project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(kind);
        var pattern = NamePattern.Create(query);
        var like = pattern.LikePatterns();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SymbolColumns} {FromSymbols} WHERE s.external = 0 AND ({string.Join(" OR ", like.Select((_, i) => $"s.name LIKE $p{i} ESCAPE '\\'"))})";
        for (var i = 0; i < like.Count; i++)
        {
            command.Parameters.AddWithValue($"$p{i}", like[i]);
        }

        var filter = kind.Trim().ToLowerInvariant();
        var trimmed = query.Trim();
        return [.. Symbols(command)
            .Where(s => pattern.MatchesName(s.Name) && pattern.MatchesContainers(s.Containers))
            .Where(s => filter == "any" || s.Match.Split(' ').Contains(filter, StringComparer.Ordinal))
            .Where(s => string.IsNullOrWhiteSpace(project) || s.Projects.Contains(project.Trim(), StringComparer.OrdinalIgnoreCase))
            .OrderBy(s => Rank(s.Name, trimmed))
            .ThenBy(s => s.Name.Length)
            .ThenBy(s => s.Id, StringComparer.Ordinal)];
    }

    /// <summary>
    /// As <see cref="SymbolLocator.ResolveAsync(WorkspaceSnapshot, Func{string, string}, string, CancellationToken)"/> over
    /// the workspace's own declarations. Empty when the index cannot tell (a symbol of a referenced assembly, a position
    /// on no indexed name): the answer then waits for the load.
    /// </summary>
    public IReadOnlyList<IndexedSymbol> Resolve(string reference, Func<string, string> resolvePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentNullException.ThrowIfNull(resolvePath);
        var text = reference.Trim();
        using var connection = Open();
        using var command = connection.CreateCommand();
        if (DocumentationId().IsMatch(text))
        {
            command.CommandText = $"SELECT {SymbolColumns} {FromSymbols} WHERE s.docid = $id AND s.external = 0";
            command.Parameters.AddWithValue("$id", SymbolFormatter.NormalizeId(text));
            return Symbols(command);
        }

        if (Location().Match(text) is { Success: true } match)
        {
            var path = resolvePath(match.Groups["path"].Value);
            var line = int.Parse(match.Groups["line"].ValueSpan, CultureInfo.InvariantCulture);
            int? column = match.Groups["column"].Success ? int.Parse(match.Groups["column"].ValueSpan, CultureInfo.InvariantCulture) : null;
            return AtPosition(connection, path, line, column);
        }

        var segments = text.Split('(')[0].Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return [];
        }

        command.CommandText = $"SELECT {SymbolColumns} {FromSymbols} WHERE s.external = 0 AND s.name = $name COLLATE NOCASE";
        command.Parameters.AddWithValue("$name", segments[^1]);
        var qualifier = segments[..^1];
        var found = Symbols(command)
            .Where(s => s.Containers.Count >= qualifier.Length && s.Containers.Skip(s.Containers.Count - qualifier.Length).SequenceEqual(qualifier, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var exact = found.Where(s => s.Name.Equals(segments[^1], StringComparison.Ordinal)).ToList();
        return exact.Count > 0 ? exact : found;
    }

    // A position: with a column, the name there; without, what the line declares, else the first name it uses.
    private static List<IndexedSymbol> AtPosition(SqliteConnection connection, string path, int line, int? column)
    {
        using var declared = connection.CreateCommand();
        declared.CommandText = $"""
            SELECT {SymbolColumns} FROM declarations d JOIN files df ON df.id = d.file JOIN symbols s ON s.id = d.symbol LEFT JOIN files f ON f.id = s.file
            WHERE df.path = $path{PathCollation} AND d.line = $line AND ($column IS NULL OR ($column >= d.col AND $column < d.col + d.length))
            ORDER BY d.col LIMIT 1
            """;
        declared.Parameters.AddWithValue("$path", path);
        declared.Parameters.AddWithValue("$line", line);
        declared.Parameters.AddWithValue("$column", (object?)column ?? DBNull.Value);
        var found = Symbols(declared);
        if (found.Count > 0)
        {
            return found;
        }

        using var used = connection.CreateCommand();
        used.CommandText = $"""
            SELECT {SymbolColumns} FROM refs r JOIN files rf ON rf.id = r.file JOIN symbols s ON s.id = r.symbol LEFT JOIN files f ON f.id = s.file
            WHERE rf.path = $path{PathCollation} AND r.line = $line AND s.external = 0 AND r.kind NOT IN ('markup', 'implicit')
              AND ($column IS NULL OR ($column >= r.col AND $column < r.col + r.length))
            ORDER BY r.col, s.match LIKE 'type%' DESC LIMIT 1
            """;
        used.Parameters.AddWithValue("$path", path);
        used.Parameters.AddWithValue("$line", line);
        used.Parameters.AddWithValue("$column", (object?)column ?? DBNull.Value);
        return Symbols(used);
    }

    /// <summary>As <see cref="ReferenceFinder.FindAsync"/>: a type's uses include its constructors', a member's those through the members it implements or overrides.</summary>
    public IndexedReferences References(IndexedSymbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        using var connection = Open();
        var through = Through(connection, symbol);
        var via = through.ToDictionary(t => t.Key, t => t.Via);
        var keys = new List<long> { symbol.Key };
        keys.AddRange(through.Select(t => t.Key));
        var constructorKeys = new List<long>();
        if (symbol.Match.StartsWith("type", StringComparison.Ordinal))
        {
            using var constructors = connection.CreateCommand();
            constructors.CommandText = "SELECT id FROM symbols WHERE parent = $parent AND kind = 'constructor'";
            constructors.Parameters.AddWithValue("$parent", symbol.Key);
            using var reader = constructors.ExecuteReader();
            while (reader.Read())
            {
                constructorKeys.Add(reader.GetInt64(0));
            }
        }

        var where = $"(r.symbol IN ({string.Join(',', keys)}) AND r.kind <> 'implicit')";
        if (constructorKeys.Count > 0)
        {
            where += $" OR (r.symbol IN ({string.Join(',', constructorKeys)}) AND r.kind NOT IN ('implicit', '{IndexSchema.TargetTypedNew}'))";
        }

        var hits = new Dictionary<(string, int, int), IndexedHit>();
        foreach (var (key, hit) in Hits(connection, where))
        {
            var marked = via.TryGetValue(key, out var viaType) ? hit with { Via = viaType } : hit;
            hits.TryAdd((hit.FilePath, hit.Line, hit.Column), marked);
        }

        using var definitions = connection.CreateCommand();
        definitions.CommandText = "SELECT f.path, d.line, d.col FROM declarations d JOIN files f ON f.id = d.file WHERE d.symbol = $symbol ORDER BY f.path, d.line";
        definitions.Parameters.AddWithValue("$symbol", symbol.Key);
        var defined = new List<IndexedHit>();
        using (var reader = definitions.ExecuteReader())
        {
            while (reader.Read())
            {
                defined.Add(new IndexedHit(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), string.Empty, (symbol.Projects.Count > 0 ? symbol.Projects[0] : string.Empty), symbol.TargetFrameworks, "definition"));
            }
        }

        return new IndexedReferences(defined, Ordered(hits.Values), [.. through]);
    }

    // The members declared in the workspace a call reaches the symbol through, and theirs in turn (ReferenceFinder.ThroughAsync).
    private static List<IndexedSymbol> Through(SqliteConnection connection, IndexedSymbol symbol)
    {
        var through = new List<IndexedSymbol>();
        var seen = new HashSet<long> { symbol.Key };
        var pending = new Queue<long>([symbol.Key]);
        while (pending.TryDequeue(out var key))
        {
            foreach (var related in Related(connection, "r.symbol = $key AND r.relation IN ('implements', 'overrides') AND s.match NOT LIKE 'type%'", key).Select(r => r.Symbol))
            {
                if (seen.Add(related.Key))
                {
                    pending.Enqueue(related.Key);
                    if (!related.IsExternal)
                    {
                        through.Add(related);
                    }
                }
            }
        }

        return through;
    }

    /// <summary>As <see cref="HierarchyFinder.FindAsync"/>: upwards as stored, downwards the stored rows read the other way.</summary>
    public IReadOnlyList<IndexedRelation> Hierarchy(IndexedSymbol symbol, string direction)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentNullException.ThrowIfNull(direction);
        var d = direction.Trim().ToUpperInvariant();
        var up = d is "ALL" or "BASE";
        var down = d is "ALL" or "DERIVED";
        var implementations = d is "ALL" or "DERIVED" or "IMPLEMENTATIONS";
        var isType = symbol.Match.StartsWith("type", StringComparison.Ordinal);
        var entries = new List<IndexedRelation>();
        using var connection = Open();
        if (up)
        {
            entries.AddRange(Related(connection, "r.symbol = $key", symbol.Key));
        }

        if (isType)
        {
            if (down && symbol.TypeKind == "Class")
            {
                entries.AddRange(Relating(connection, "base", symbol.Key).Select(s => new IndexedRelation("derived", s, s.Signature)));
            }

            if (down && symbol.TypeKind == "Interface")
            {
                entries.AddRange(Relating(connection, "implements", symbol.Key).Where(s => s.TypeKind == "Interface").Select(s => new IndexedRelation("derived interface", s, s.Signature)));
            }

            if (implementations && symbol.TypeKind == "Interface")
            {
                entries.AddRange(Relating(connection, "implements", symbol.Key).Where(s => s.TypeKind != "Interface").Select(s => new IndexedRelation("implementation", s, s.Signature)));
            }
        }
        else
        {
            if (down)
            {
                entries.AddRange(Relating(connection, "overrides", symbol.Key).Select(s => new IndexedRelation("overridden by", s, s.Signature)));
            }

            if (implementations && (symbol.TypeKind == "Interface" || symbol.IsAbstract))
            {
                entries.AddRange(Relating(connection, symbol.TypeKind == "Interface" ? "implements" : "overrides", symbol.Key).Select(s => new IndexedRelation("implemented by", s, s.Signature)));
            }
        }

        return [.. entries.DistinctBy(e => (e.Relation, e.Symbol.Id))];
    }

    /// <summary>As <see cref="CallHierarchyFinder.FindAsync"/>: callers are the members the uses are in, callees what a member calls or creates.</summary>
    public IReadOnlyList<IndexedCallNode> Calls(IndexedSymbol symbol, string direction, int depth)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentNullException.ThrowIfNull(direction);
        using var connection = Open();
        var budget = CallHierarchyFinder.MaxNodes;
        var visited = new HashSet<long> { symbol.Key };
        var callers = direction.Trim().Equals("callers", StringComparison.OrdinalIgnoreCase);
        return Level(symbol, Math.Clamp(depth, 1, CallHierarchyFinder.MaxDepth));

        List<IndexedCallNode> Level(IndexedSymbol from, int left)
        {
            // Callers as Roslyn finds them: of the member, of the members it overrides or implements (a call there may
            // reach it), and of its overrides and implementations. Callees: the methods and constructors it calls.
            var keys = new HashSet<long> { from.Key };
            if (callers)
            {
                keys.UnionWith(Related(connection, "r.symbol = $key AND r.relation IN ('implements', 'overrides')", from.Key).Select(r => r.Symbol.Key));
                keys.UnionWith(Relating(connection, "implements", from.Key).Concat(Relating(connection, "overrides", from.Key)).Select(s => s.Key));
            }

            var sql = callers
                ? $"SELECT {SymbolColumns}, rf.path, r.line FROM refs r JOIN files rf ON rf.id = r.file JOIN symbols s ON s.id = r.container LEFT JOIN files f ON f.id = s.file WHERE r.symbol IN ({string.Join(',', keys)}) AND r.kind <> 'markup'"
                : $"SELECT {SymbolColumns}, rf.path, r.line FROM refs r JOIN files rf ON rf.id = r.file JOIN symbols s ON s.id = r.symbol LEFT JOIN files f ON f.id = s.file WHERE r.container = $key AND r.kind IN ('call', 'new', 'implicit', '{IndexSchema.TargetTypedNew}') AND s.match = 'method'";
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$key", from.Key);
            var found = new Dictionary<string, (IndexedSymbol Symbol, SortedSet<CallSite> Sites)>(StringComparer.Ordinal);
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var related = Symbol(reader);
                    if (!found.TryGetValue(related.Id, out var entry))
                    {
                        entry = (related, new SortedSet<CallSite>(Comparer<CallSite>.Create((a, b) =>
                            StringComparer.OrdinalIgnoreCase.Compare(a.FilePath, b.FilePath) is var c and not 0 ? c : a.Line.CompareTo(b.Line))));
                        found[related.Id] = entry;
                    }

                    entry.Sites.Add(new CallSite(reader.GetString(24), reader.GetInt32(25)));
                }
            }

            var nodes = new List<IndexedCallNode>();
            foreach (var (_, (related, sites)) in found.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (budget-- <= 0)
                {
                    break;
                }

                var children = left > 1 && !related.IsExternal && visited.Add(related.Key) ? Level(related, left - 1) : [];
                nodes.Add(new IndexedCallNode(related, [.. sites], children));
            }

            return nodes;
        }
    }

    /// <summary>As <see cref="OutlineBuilder.ForDocumentAsync"/>: the declarations in one file, by line.</summary>
    public IReadOnlyList<IndexedDeclaration> OutlineOfFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SymbolColumns}, df.path, d.line FROM declarations d JOIN files df ON df.id = d.file JOIN symbols s ON s.id = d.symbol LEFT JOIN files f ON f.id = s.file
            WHERE df.path = $path{PathCollation} ORDER BY d.line, s.depth
            """;
        command.Parameters.AddWithValue("$path", Path.GetFullPath(path));
        return Declarations(command).DistinctBy(d => (d.Symbol.Key, d.Line)).ToList();
    }

    /// <summary>Every declaration of the workspace, for tests that compare the index with the load.</summary>
    internal IReadOnlyList<IndexedSymbol> AllSymbols()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SymbolColumns} {FromSymbols} WHERE s.external = 0 ORDER BY s.docid";
        return Symbols(command);
    }

    /// <summary>Every source declaration of the symbol (several for a partial type), by file and line.</summary>
    public IReadOnlyList<(string Path, int Line)> DeclaredAt(IndexedSymbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT f.path, d.line FROM declarations d JOIN files f ON f.id = d.file WHERE d.symbol = $symbol ORDER BY d.rowid";
        command.Parameters.AddWithValue("$symbol", symbol.Key);
        var declared = new List<(string, int)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            declared.Add((reader.GetString(0), reader.GetInt32(1)));
        }

        return declared;
    }

    /// <summary>Whether the index knows the file (as a source file): a path it does not know is answered by the load.</summary>
    public bool Knows(string path)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT 1 FROM files WHERE path = $path{PathCollation} AND kind = 0";
        command.Parameters.AddWithValue("$path", Path.GetFullPath(path));
        return command.ExecuteScalar() is not null;
    }

    /// <summary>As <see cref="OutlineBuilder.ForType"/>: a type, its members and its nested types' members, from every partial declaration.</summary>
    public IReadOnlyList<IndexedDeclaration> OutlineOfType(IndexedSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SymbolColumns}, f.path, s.line {FromSymbols}
            WHERE s.external = 0 AND (s.id = $key OR s.parent = $key OR s.parent IN (SELECT id FROM symbols WHERE parent = $key AND match LIKE 'type%'))
            """;
        command.Parameters.AddWithValue("$key", type.Key);
        return [.. Declarations(command)
            .Where(d => d.FilePath.Length > 0)
            .OrderBy(d => d.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Line)];
    }

    private static List<IndexedDeclaration> Declarations(SqliteCommand command)
    {
        var entries = new List<IndexedDeclaration>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var symbol = Symbol(reader);
            entries.Add(new IndexedDeclaration(symbol.Depth, symbol, reader.IsDBNull(24) ? string.Empty : reader.GetString(24), reader.GetInt32(25)));
        }

        return entries;
    }

    // Symbols related to the key through the stored (upward) rows.
    private static List<IndexedRelation> Related(SqliteConnection connection, string where, long key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SymbolColumns}, r.relation, r.signature FROM relations r JOIN symbols s ON s.id = r.target LEFT JOIN files f ON f.id = s.file WHERE {where} ORDER BY r.ordinal";
        command.Parameters.AddWithValue("$key", key);
        var related = new List<IndexedRelation>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            related.Add(new IndexedRelation(reader.GetString(24), Symbol(reader), reader.GetString(25)));
        }

        return related;
    }

    // Symbols of the workspace that name the key in a stored row: the downward direction.
    private static List<IndexedSymbol> Relating(SqliteConnection connection, string relation, long key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SymbolColumns} FROM relations r JOIN symbols s ON s.id = r.symbol LEFT JOIN files f ON f.id = s.file WHERE r.target = $key AND r.relation = $relation AND s.external = 0";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$relation", relation);
        return Symbols(command);
    }

    private static List<(long Symbol, IndexedHit Hit)> Hits(SqliteConnection connection, string where)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT r.symbol, f.path, r.line, r.col, COALESCE(l.text, ''), p.text, w.text, r.kind
            FROM refs r JOIN files f ON f.id = r.file JOIN labels p ON p.id = r.project JOIN labels w ON w.id = r.frameworks
            LEFT JOIN lines l ON l.file = r.file AND l.line = r.line
            WHERE {where}
            """;
        var hits = new List<(long, IndexedHit)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            hits.Add((reader.GetInt64(0), new IndexedHit(reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetString(4), reader.GetString(5), IndexSchema.Split(reader.GetString(6)), reader.GetString(7) is IndexSchema.TargetTypedNew ? "new" : reader.GetString(7))));
        }

        return hits;
    }

    private static List<IndexedHit> Ordered(IEnumerable<IndexedHit> hits) =>
        [.. hits
            .OrderBy(h => h.Project, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.Line)
            .ThenBy(h => h.Column)];

    private static List<IndexedSymbol> Symbols(SqliteCommand command)
    {
        var symbols = new List<IndexedSymbol>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            symbols.Add(Symbol(reader));
        }

        return symbols;
    }

    private static IndexedSymbol Symbol(SqliteDataReader r) => new(
        r.GetInt64(0),
        r.GetString(1),
        r.GetString(2),
        r.GetString(3),
        r.GetString(4),
        r.GetString(5),
        r.GetString(6),
        IndexSchema.Split(r.GetString(7)),
        IndexSchema.Split(r.GetString(8)),
        r.IsDBNull(9) ? null : r.GetString(9),
        IndexSchema.Split(r.GetString(10)),
        r.IsDBNull(11) ? null : r.GetString(11),
        r.IsDBNull(12) ? null : r.GetString(12),
        r.GetInt32(13),
        r.GetInt32(14),
        r.GetInt32(15),
        r.GetInt32(16) != 0,
        r.IsDBNull(17) ? null : r.GetString(17),
        r.GetString(18),
        r.GetInt32(19),
        IndexSchema.Split(r.GetString(20)),
        r.GetString(21),
        r.GetInt32(22) != 0,
        r.GetString(23));

    private SqliteConnection Open()
    {
        if (File.GetLastWriteTimeUtc(FilePath).Ticks != _fileTicks)
        {
            throw new IndexReplacedException(FilePath);
        }

        return IndexSchema.Open(FilePath, readOnly: true);
    }

    private static int Rank(string name, string query)
    {
        if (string.Equals(name, query, StringComparison.Ordinal))
        {
            return 0;
        }

        if (string.Equals(name, query, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return name.Contains(query, StringComparison.OrdinalIgnoreCase) ? 3 : 4;
    }

    [GeneratedRegex(@"^[TMPFEN]:(?![\\/])\S+$")]
    private static partial Regex DocumentationId();

    [GeneratedRegex(@"^(?<path>.+?\.(cs|vb)):(?<line>\d+)(:(?<column>\d+))?$", RegexOptions.IgnoreCase)]
    private static partial Regex Location();
}
