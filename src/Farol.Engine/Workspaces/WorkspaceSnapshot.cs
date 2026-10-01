using Microsoft.CodeAnalysis;

namespace Farol.Engine.Workspaces;

/// <summary>
/// One immutable view of a workspace. Every tool request runs on exactly one snapshot; file changes
/// produce the next version.
/// </summary>
public sealed record WorkspaceSnapshot(Solution Solution, int Version, ProjectCatalog Projects);
