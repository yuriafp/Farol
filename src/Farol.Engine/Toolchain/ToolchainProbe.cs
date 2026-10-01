using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using Farol.Core.Execution;

namespace Farol.Engine.Toolchain;

public sealed record VisualStudioInstance(string DisplayName, string Version, string InstallationPath, string? MSBuildPath)
{
    /// <summary>"17.14.37710.0" → "17.14".</summary>
    public string ShortVersion => string.Join('.', Version.Split('.').Take(2));

    /// <summary>The VSTest runner for classic .NET Framework test projects, when the testing tools are installed.</summary>
    public string? VSTestPath =>
        Path.Combine(InstallationPath, "Common7", "IDE", "Extensions", "TestPlatform", "vstest.console.exe") is var path && File.Exists(path) ? path : null;
}

public sealed record ToolchainInfo(string? DotnetSdkVersion, IReadOnlyList<VisualStudioInstance> VisualStudio)
{
    /// <summary>The newest Visual Studio / Build Tools MSBuild, required by classic web and WPF projects.</summary>
    public VisualStudioInstance? PreferredVisualStudio =>
        VisualStudio
            .Where(v => v.MSBuildPath is not null)
            .OrderByDescending(v => System.Version.TryParse(v.Version, out var parsed) ? parsed : new Version())
            .FirstOrDefault();
}

/// <summary>
/// Detects the .NET SDK a directory resolves to (global.json aware) and the Visual Studio / Build Tools
/// installations whose MSBuild can build classic .NET Framework projects. Cached per directory.
/// </summary>
public sealed class ToolchainProbe(IProcessRunner runner)
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);
    private static readonly string[] VsWhereArguments =
        ["-all", "-prerelease", "-products", "*", "-requires", "Microsoft.Component.MSBuild", "-format", "json", "-utf8"];

    private readonly ConcurrentDictionary<string, Lazy<Task<ToolchainInfo>>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public Task<ToolchainInfo> ProbeAsync(string directory, CancellationToken cancellationToken) =>
        _cache.GetOrAdd(directory, d => new Lazy<Task<ToolchainInfo>>(() => ProbeCoreAsync(d))).Value.WaitAsync(cancellationToken);

    private async Task<ToolchainInfo> ProbeCoreAsync(string directory)
    {
        var sdk = ProbeDotnetSdkAsync(directory);
        var visualStudio = OperatingSystem.IsWindows() ? ProbeVisualStudioAsync(directory) : Task.FromResult<IReadOnlyList<VisualStudioInstance>>([]);
        return new ToolchainInfo(await sdk, await visualStudio);
    }

    private async Task<string?> ProbeDotnetSdkAsync(string directory)
    {
        try
        {
            var result = await runner.RunAsync(new ProcessSpec("dotnet", ["--version"], directory, ProbeTimeout), CancellationToken.None);
            return result.ExitCode == 0 ? result.StandardOutput.Trim() : null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<VisualStudioInstance>> ProbeVisualStudioAsync(string directory)
    {
        var vswhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere))
        {
            return [];
        }

        var result = await runner.RunAsync(new ProcessSpec(vswhere, VsWhereArguments, directory, ProbeTimeout), CancellationToken.None);
        if (result.ExitCode != 0)
        {
            return [];
        }

        using var json = JsonDocument.Parse(result.StandardOutput);
        var instances = new List<VisualStudioInstance>();
        foreach (var item in json.RootElement.EnumerateArray())
        {
            if (item.GetProperty("installationPath").GetString() is not { } path)
            {
                continue;
            }

            var msbuild = Path.Combine(path, "MSBuild", "Current", "Bin", "MSBuild.exe");
            instances.Add(new VisualStudioInstance(
                PlainSpaces(item.TryGetProperty("displayName", out var name) ? name.GetString() ?? "Visual Studio" : "Visual Studio"),
                item.TryGetProperty("installationVersion", out var version) ? version.GetString() ?? string.Empty : string.Empty,
                path,
                File.Exists(msbuild) ? msbuild : null));
        }

        return instances;
    }

    // Some installers name themselves with non-breaking spaces ("Visual Studio"): agents searching the text would miss them.
    private static string PlainSpaces(string text) =>
        string.Concat(text.Select(c => char.IsWhiteSpace(c) ? ' ' : c));
}
