using Farol.Core.Paths;
using Farol.Engine.Toolchain;

namespace Farol.Engine.Analysis;

/// <summary>Knows which build command actually works for a solution — the first thing agents get wrong on legacy code.</summary>
public static class BuildAdvisor
{
    public static string SuggestBuildCommand(SolutionOverview overview, ToolchainInfo toolchain, string root)
    {
        ArgumentNullException.ThrowIfNull(overview);
        ArgumentNullException.ThrowIfNull(toolchain);

        var target = DisplayPath.From(root, overview.Target.Path);
        if (overview.Projects.All(p => p.IsSdkStyle))
        {
            return $"dotnet build \"{target}\"";
        }

        // Classic web (WebApplication targets), WPF (WinFX targets) and resx-heavy projects need
        // Visual Studio's .NET Framework MSBuild; `dotnet build` fails on them.
        var restore = overview.Projects.Any(p => p.HasPackagesConfig) ? " -p:RestorePackagesConfig=true" : string.Empty;
        return toolchain.PreferredVisualStudio?.MSBuildPath is { } msbuild
            ? $"\"{msbuild}\" \"{target}\" -restore{restore} -nodeReuse:false -v:minimal"
            : $"msbuild \"{target}\" -restore{restore} (classic projects need Visual Studio or Build Tools MSBuild; none was found)";
    }
}
