using Microsoft.Data.Sqlite;

namespace Farol.Engine.Indexing;

/// <summary>
/// The tables of a persistent index. <see cref="FormatVersion"/> changes with any change here: an index of another
/// version is ignored and replaced (AC-52). Paths are full paths; text is only the one line shown with each reference.
/// </summary>
internal static class IndexSchema
{
    public const int FormatVersion = 1;

    // File kinds: what a change to one means for an answer.
    public const int SourceFile = 0;
    public const int ProjectFile = 1;
    public const int MarkupFile = 2;

    /// <summary>
    /// The kind of a target-typed <c>new()</c>: a "new" reference of the constructor called, not of its type (Roslyn's
    /// reference search lists it only for the constructor).
    /// </summary>
    public const string TargetTypedNew = "new()";

    public const string Create = """
        CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE files(id INTEGER PRIMARY KEY, path TEXT NOT NULL, kind INTEGER NOT NULL, ticks INTEGER NOT NULL, size INTEGER NOT NULL);
        CREATE TABLE labels(id INTEGER PRIMARY KEY, text TEXT NOT NULL);
        CREATE TABLE symbols(
            id INTEGER PRIMARY KEY, docid TEXT NOT NULL, name TEXT NOT NULL, kind TEXT NOT NULL, match TEXT NOT NULL,
            containers TEXT NOT NULL, signature TEXT NOT NULL, member TEXT NOT NULL, namespace TEXT NOT NULL,
            projects TEXT NOT NULL, frameworks TEXT NOT NULL, summary TEXT, inherits TEXT NOT NULL, via TEXT, parent INTEGER,
            depth INTEGER NOT NULL, language TEXT NOT NULL, file INTEGER, line INTEGER NOT NULL, start_line INTEGER NOT NULL,
            end_line INTEGER NOT NULL, external INTEGER NOT NULL, assembly TEXT, abstract INTEGER NOT NULL, type_kind TEXT NOT NULL);
        CREATE TABLE declarations(symbol INTEGER NOT NULL, file INTEGER NOT NULL, line INTEGER NOT NULL, col INTEGER NOT NULL, length INTEGER NOT NULL);
        CREATE TABLE lines(file INTEGER NOT NULL, line INTEGER NOT NULL, text TEXT NOT NULL, PRIMARY KEY(file, line)) WITHOUT ROWID;
        CREATE TABLE refs(
            symbol INTEGER NOT NULL, file INTEGER NOT NULL, line INTEGER NOT NULL, col INTEGER NOT NULL, length INTEGER NOT NULL,
            kind TEXT NOT NULL, project INTEGER NOT NULL, frameworks INTEGER NOT NULL, container INTEGER);
        CREATE TABLE relations(symbol INTEGER NOT NULL, relation TEXT NOT NULL, target INTEGER NOT NULL, ordinal INTEGER NOT NULL, signature TEXT NOT NULL);
        """;

    // Created after the rows are in: one sort per index instead of an update per row.
    public const string Indexes = """
        CREATE UNIQUE INDEX symbols_docid ON symbols(docid);
        CREATE INDEX symbols_name ON symbols(name COLLATE NOCASE);
        CREATE INDEX symbols_parent ON symbols(parent);
        CREATE UNIQUE INDEX files_path ON files(path);
        CREATE INDEX declarations_file ON declarations(file, line);
        CREATE INDEX declarations_symbol ON declarations(symbol);
        CREATE INDEX refs_symbol ON refs(symbol);
        CREATE INDEX refs_container ON refs(container);
        CREATE INDEX refs_file ON refs(file, line);
        CREATE INDEX relations_symbol ON relations(symbol);
        CREATE INDEX relations_target ON relations(target, relation);
        """;

    public static SqliteConnection Open(string path, bool readOnly)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,

            // No pooled handle may keep the file open: a newer index replaces it, and retention deletes it.
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    /// <summary>Lists stored in one column: project names, frameworks, container names, base types.</summary>
    public const char Separator = '\u001f';

    public static string Join(IEnumerable<string> values) => string.Join(Separator, values);

    public static IReadOnlyList<string> Split(string? value) =>
        string.IsNullOrEmpty(value) ? [] : value.Split(Separator);
}
