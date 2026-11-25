namespace Farol.Engine.Workspaces;

public enum WorkspaceTargetKind
{
    Solution,
    SolutionFilter,
    Project,

    /// <summary>An MSBuild binary log (.binlog): the workspace is what that build compiled.</summary>
    BinaryLog,
}

/// <summary>What to open: a solution (.sln/.slnx), a solution filter (.slnf), a single project or a binary log (.binlog).</summary>
public sealed record WorkspaceTarget(string Path, WorkspaceTargetKind Kind)
{
    public string Directory => System.IO.Path.GetDirectoryName(Path)!;

    public string DisplayName => System.IO.Path.GetFileName(Path);
}
