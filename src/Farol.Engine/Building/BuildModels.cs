namespace Farol.Engine.Building;

/// <summary>An error or warning from a build. Reported by several target-framework builds of a project, it is one issue listing them.</summary>
public sealed record BuildIssue(
    bool IsError,
    string? Code,
    string Message,
    string? FilePath,
    int Line,
    int Column,
    string Project,
    IReadOnlyList<string> TargetFrameworks);

/// <summary>What a build did: its outcome, the issues read from the binary log and the files each project produced.</summary>
public sealed record BuildResult(
    bool Succeeded,
    int ExitCode,
    bool TimedOut,
    TimeSpan Elapsed,
    string Toolchain,
    string Target,
    string Configuration,
    IReadOnlyList<BuildIssue> Errors,
    IReadOnlyList<BuildIssue> Warnings,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Outputs,
    string? BinaryLog,
    string? OutputTail);
