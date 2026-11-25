using System.ComponentModel;
using System.Globalization;
using Farol.Core;
using Farol.Core.Paths;
using Farol.Core.Text;
using Farol.Engine.Indexing;
using Farol.Engine.Navigation;
using Farol.Engine.Workspaces;

namespace Farol.Tools.Features.Navigation;

/// <summary>Set when an answer comes from the persistent index because the workspace is still loading (AC-50, AC-51).</summary>
public sealed record IndexNote(
    [property: Description("When the index was written (UTC, ISO 8601).")] string WrittenAt,
    [property: Description("Files changed since the index was written; results in them are marked and may be out of date.")] IReadOnlyList<string> ChangedFiles,
    [property: Description("True when a project or solution file changed since the index: the projects themselves may have changed.")] bool ProjectsMayHaveChanged);

/// <summary>
/// Navigation and dotnet_overview before the load is done: answered from the workspace's persistent index, said so with
/// when it was written and which files changed since, results in those files marked. Once the load is done, answers come
/// from the load again; tools that need the compiler (dotnet_check, code actions, build, test) always wait for it.
/// </summary>
internal sealed class IndexedAnswers
{
    public const string ChangedMark = " · changed since the index";

    private IndexedAnswers(WorkspaceIndex index, IndexFreshness freshness, WorkspaceSession session, string root)
    {
        Index = index;
        Freshness = freshness;
        Session = session;
        Root = root;
    }

    public WorkspaceIndex Index { get; }

    public IndexFreshness Freshness { get; }

    public WorkspaceSession Session { get; }

    public string Root { get; }

    public IndexNote Note => new(
        Freshness.WrittenAt.ToString("O", CultureInfo.InvariantCulture),
        [.. Freshness.ChangedFiles.Select(f => DisplayPath.From(Root, f))],
        Freshness.ProjectsChanged);

    /// <summary>
    /// The index to answer from: only while the session's load is running (it starts it), never once it is done, and
    /// never under <c>Farol:Index=false</c>.
    /// </summary>
    private static IndexedAnswers? Before(WorkspaceManager workspaces, WorkspaceSession session)
    {
        // A load that failed is reported as it was without an index, not hidden behind answers that say it is loading.
        if (session.CurrentSnapshot is not null || session.State == WorkspaceState.Failed)
        {
            return null;
        }

        _ = session.StartLoading();
        if (session.CurrentSnapshot is not null || session.State != WorkspaceState.Loading || workspaces.Indexes.Open(session.Target) is not { } index)
        {
            return null;
        }

        return new IndexedAnswers(index, index.Freshness(), session, workspaces.RootDirectory);
    }

    /// <summary>
    /// The answer from the index (see <see cref="Before"/>), or null to answer from the load: when there is no index to
    /// answer from, when it cannot tell, and when reading it fails (it is then not used again, and deleted when unreadable).
    /// </summary>
    public static T? Answer<T>(WorkspaceManager workspaces, WorkspaceSession session, Func<IndexedAnswers, T?> answer)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(answer);
        try
        {
            return Before(workspaces, session) is { } indexed ? answer(indexed) : null;
        }
#pragma warning disable CA1031 // The index is an optimization: whatever goes wrong reading it, the load answers.
        catch (Exception ex) when (ex is not (OperationCanceledException or FarolException))
#pragma warning restore CA1031
        {
            workspaces.Indexes.Discard(session.Target, ex);
            return null;
        }
    }

    /// <summary>The lines that open every answer from the index.</summary>
    public void Header(ResponseBuilder text)
    {
        text.Line($"from the index written {Freshness.WrittenAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC: the workspace is still loading ({Seconds(Session.LoadingFor)} so far), and answers come from the load once it is done.");
        if (Freshness.ProjectsChanged)
        {
            text.Line("project or solution files changed since the index: the projects may have changed.");
        }

        if (Freshness.ChangedFiles.Count > 0)
        {
            text.Line($"changed since the index: {DisplayPath.Places(Root, Freshness.ChangedFiles, max: 5)}; results in them are marked and may be out of date.");
        }
    }

    public string Mark(string? path) => Freshness.IsChanged(path) ? ChangedMark : string.Empty;

    public string Location(IndexedSymbol symbol) =>
        symbol.FilePath is { } path ? DisplayPath.Location(Root, path, symbol.Line) : $"metadata: {symbol.Assembly ?? "unknown"}";

    public string Candidate(IndexedSymbol symbol)
    {
        var frameworks = symbol.TargetFrameworks.Count > 1 ? $" · {string.Join(", ", symbol.TargetFrameworks)}" : string.Empty;
        return $"{symbol.Display} · {Location(symbol)}{frameworks} · id {symbol.Id}{Mark(symbol.FilePath)}";
    }

    public string Symbol(IndexedSymbol symbol) => $"{symbol.Display} · {Location(symbol)}{Mark(symbol.FilePath)}";

    public SymbolItem Item(IndexedSymbol symbol) => new(symbol.Signature, symbol.Kind, Location(symbol), symbol.Id, symbol.TargetFrameworks);

    public static SourceHit Hit(IndexedHit hit) => new(hit.FilePath, hit.Line, hit.Column, hit.Snippet, hit.Project, hit.TargetFrameworks, hit.Kind, hit.Via);

    /// <summary>
    /// As <see cref="SymbolArgument.ResolveAsync"/> over the index. Null when the index cannot tell (a symbol of a referenced
    /// assembly, a file it does not know): the call then waits for the load and answers from it.
    /// </summary>
    public (IndexedSymbol? Symbol, string? Ambiguity, IReadOnlyList<SymbolItem>? Candidates)? Resolve(PathSandbox paths, string symbol)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new FarolException(ErrorCodes.InvalidArgument, "'symbol' is empty.", "Pass a name, a dotted name, a documentation comment ID or a path:line position.");
        }

        // A position in a file changed since the index points at what is there now, which the index does not know.
        if (SymbolLocator.LocationPath(symbol) is { } location && Freshness.IsChanged(paths.Resolve(location, Session.Target.Directory)))
        {
            return null;
        }

        var candidates = Index.Resolve(symbol, path => paths.Resolve(path, Session.Target.Directory));
        if (candidates.Count == 0)
        {
            return null;
        }

        if (candidates.Count == 1)
        {
            return (candidates[0], null, null);
        }

        var text = new ResponseBuilder(TokenBudget.DefaultTokens);
        Header(text);
        text.Line($"'{symbol}' matches {candidates.Count} symbols. Call again with the id of the one you mean:");
        text.List("candidates", candidates, Candidate, continuation: _ => "use a more specific name");
        return (null, text.ToString(), [.. text.WindowOf("candidates").Of(candidates).Select(Item)]);
    }

    private static string Seconds(TimeSpan elapsed) => $"{elapsed.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s";
}
