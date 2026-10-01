namespace Farol.Engine.Workspaces;

public enum WorkspaceTargetKind
{
    Solution,
    SolutionFilter,
    Project,
}

/// <summary>What to open: a solution (.sln/.slnx), a solution filter (.slnf) or a single project.</summary>
public sealed record WorkspaceTarget(string Path, WorkspaceTargetKind Kind)
{
    public string Directory => System.IO.Path.GetDirectoryName(Path)!;

    public string DisplayName => System.IO.Path.GetFileName(Path);
}
