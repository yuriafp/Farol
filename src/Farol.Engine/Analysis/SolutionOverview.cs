using Farol.Engine.Workspaces;

namespace Farol.Engine.Analysis;

public sealed record ProjectOverview(
    string Name,
    string FilePath,
    string Language,
    bool IsSdkStyle,
    IReadOnlyList<string> TargetFrameworks,
    string OutputKind,
    IReadOnlyList<string> AppModels,
    IReadOnlyList<string> TestFrameworks,
    IReadOnlyList<string> ProjectReferences,
    int DocumentCount,
    bool HasPackagesConfig)
{
    /// <summary>True when any target is .NET Framework (net20 … net481).</summary>
    public bool TargetsNetFramework => TargetFrameworks.Any(TargetFrameworkNames.IsNetFramework);
}

public sealed record SolutionOverview(
    WorkspaceTarget Target,
    IReadOnlyList<ProjectOverview> Projects,
    bool CentralPackageManagement,
    string? GlobalJsonSdk,
    IReadOnlyList<string> BindingRedirectFiles);

public static class TargetFrameworkNames
{
    /// <summary>"net48" or "net472" (digits only) is .NET Framework; "net10.0" and "netstandard2.0" are not.</summary>
    public static bool IsNetFramework(string tfm) =>
        tfm.Length > 3
        && tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase)
        && tfm.AsSpan(3).IndexOfAnyExceptInRange('0', '9') < 0;
}
