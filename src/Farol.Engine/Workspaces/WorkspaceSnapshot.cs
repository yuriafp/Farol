using Microsoft.CodeAnalysis;

namespace Farol.Engine.Workspaces;

/// <summary>
/// One immutable view of a workspace. Every tool request runs on exactly one snapshot; file changes
/// produce the next version.
/// </summary>
public sealed record WorkspaceSnapshot(Solution Solution, int Version, ProjectCatalog Projects, WorkspaceLoad Load, WorkspaceTarget Target);

/// <summary>
/// The workspace exactly as the last load (or reload) produced it: what dotnet_check compares against, so
/// only diagnostics introduced since then are reported as new. <paramref name="ChangedDuringLoad"/> holds the source
/// files that changed before the load was done: the load may have read them already changed, so they are no baseline.
/// </summary>
public sealed record WorkspaceLoad(Solution Solution, DateTimeOffset LoadedAt, IReadOnlySet<string> ChangedDuringLoad)
{
    /// <summary>The loader that produced this load: "msbuild-workspace" (full fidelity) or "heuristic".</summary>
    public string Loader { get; init; } = "msbuild-workspace";

    /// <summary>Whether the load read classic projects without MSBuild: generated code is missing, and build and test need MSBuild.</summary>
    public bool IsHeuristic => Loader == "heuristic";

    /// <summary>
    /// Renames Farol applied since this load (old name, new name): a load diagnostic whose line the rename rewrote is
    /// still the same diagnostic. Shared by every snapshot of the load; a new load starts empty.
    /// </summary>
    public System.Collections.Concurrent.ConcurrentQueue<(string Old, string New)> Renames { get; } = new();

    /// <summary>
    /// Source files Farol moved since this load (old path, new path): a load diagnostic of the old path is the same
    /// diagnostic at the new one. Shared by every snapshot of the load; a new load starts empty.
    /// </summary>
    public System.Collections.Concurrent.ConcurrentQueue<(string From, string To)> Moves { get; } = new();

    /// <summary>
    /// Lines Farol's refactorings rewrote since this load (file, old text, new text, trimmed): a load diagnostic on such
    /// a line, with the same message, is still the same diagnostic. A new load starts empty.
    /// </summary>
    public System.Collections.Concurrent.ConcurrentQueue<(string Path, string Old, string New)> LineEdits { get; } = new();
}
