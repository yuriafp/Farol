namespace Farol.Engine.Workspaces;

public sealed record LoadIssue(string Severity, string Message);

/// <summary>What a load produced: counts, target frameworks per project file and every issue MSBuild reported.</summary>
public sealed record LoadReport(
    string Loader,
    TimeSpan Elapsed,
    int ProjectFiles,
    int RoslynProjects,
    int Documents,
    IReadOnlyDictionary<string, IReadOnlyList<string>> TargetFrameworks,
    IReadOnlyList<LoadIssue> Issues);
