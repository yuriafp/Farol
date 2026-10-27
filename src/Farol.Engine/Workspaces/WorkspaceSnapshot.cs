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
public sealed record WorkspaceLoad(Solution Solution, DateTimeOffset LoadedAt, IReadOnlySet<string> ChangedDuringLoad);
