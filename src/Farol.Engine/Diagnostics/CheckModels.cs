using Microsoft.CodeAnalysis;

namespace Farol.Engine.Diagnostics;

public enum CheckScope
{
    /// <summary>Files changed since the workspace loaded, plus the projects their declaration changes can break.</summary>
    Changed,
    File,
    Project,
    Solution,
}

/// <summary>What dotnet_check looks at. <paramref name="IncludeExisting"/> also returns what was already there at load.</summary>
public sealed record CheckRequest(CheckScope Scope, string? FilePath = null, string? Project = null, bool IncludeExisting = false);

/// <summary>One compiler diagnostic. Found in several target frameworks of a project, it is one entry listing them.</summary>
public sealed record DiagnosticEntry(
    string Id,
    DiagnosticSeverity Severity,
    string Message,
    string? FilePath,
    int Line,
    int Column,
    string Project,
    IReadOnlyList<string> TargetFrameworks);

/// <summary>
/// New diagnostics (absent from the load baseline) and, when asked, the existing ones, plus what was checked:
/// whole projects when declarations changed, single files when only member bodies did.
/// </summary>
public sealed record CheckResult(
    IReadOnlyList<DiagnosticEntry> New,
    IReadOnlyList<DiagnosticEntry> Existing,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> CheckedProjects,
    IReadOnlyList<string> CheckedFiles);
