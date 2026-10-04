using System.Collections.Concurrent;
using System.Diagnostics;
using Farol.Engine.Packages;
using Farol.Engine.Toolchain;
using Farol.Engine.Workspaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;

namespace Farol.Engine.Loading;

public sealed record LoadedWorkspace(MSBuildWorkspace Workspace, Solution Solution, LoadReport Report);

/// <summary>
/// Loads solutions through Roslyn's MSBuildWorkspace. MSBuild runs out of process in a BuildHost:
/// SDK-style projects use the .NET SDK host, and classic .NET Framework projects use the net472 host
/// with Visual Studio's MSBuild — which is what gives legacy projects full fidelity.
/// </summary>
public sealed partial class MSBuildWorkspaceLoader(ToolchainProbe toolchain, ILogger<MSBuildWorkspaceLoader> logger)
{
    public const string Name = "msbuild-workspace";

    public async Task<LoadedWorkspace> LoadAsync(
        WorkspaceTarget target,
        IReadOnlyDictionary<string, string> properties,
        Action<ProjectLoadProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(properties);

        var stopwatch = Stopwatch.StartNew();
        var frameworks = new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>(StringComparer.OrdinalIgnoreCase);
        var progress = new SynchronousProgress<ProjectLoadProgress>(p =>
        {
            if (!string.IsNullOrEmpty(p.TargetFramework))
            {
                frameworks.GetOrAdd(p.FilePath, _ => new(StringComparer.OrdinalIgnoreCase)).TryAdd(p.TargetFramework, 0);
            }

            onProgress?.Invoke(p);
        });

        var global = new Dictionary<string, string>(properties, StringComparer.OrdinalIgnoreCase);
        if (!global.ContainsKey("VSToolsPath") && await VisualStudioToolsPathAsync(target.Directory, cancellationToken) is { } vsTools)
        {
            global["VSToolsPath"] = vsTools;
        }

        var workspace = MSBuildWorkspace.Create(global);
        try
        {
            var solution = target.Kind == WorkspaceTargetKind.Project
                ? (await workspace.OpenProjectAsync(target.Path, progress, cancellationToken)).Solution
                : await workspace.OpenSolutionAsync(target.Path, progress, cancellationToken);
            (solution, var missingAnalyzers) = WithoutUnresolvedAnalyzers(solution);
            await ReadAllTextAsync(solution, cancellationToken);
            stopwatch.Stop();

            var report = new LoadReport(
                Name,
                stopwatch.Elapsed,
                ProjectFiles: solution.Projects.Select(p => p.FilePath).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                RoslynProjects: solution.ProjectIds.Count,
                Documents: solution.Projects.SelectMany(p => p.Documents).Select(d => d.FilePath ?? d.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                TargetFrameworks: frameworks.ToDictionary(
                    kv => kv.Key,
                    kv => (IReadOnlyList<string>)[.. kv.Value.Keys.Order(StringComparer.OrdinalIgnoreCase)],
                    StringComparer.OrdinalIgnoreCase),
                Issues: [.. Issues(workspace.Diagnostics, solution), .. missingAnalyzers]);

            LogLoaded(logger, target.Path, report.ProjectFiles, report.Documents, report.Issues.Count, report.Elapsed);
            return new LoadedWorkspace(workspace, solution, report);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    /// <summary>
    /// MSBuildWorkspace reports every message of a design-time build as a failure, warnings included. Design-time builds
    /// replay the warnings restore recorded (vulnerable packages, version conflicts…): those are warnings, with their
    /// NuGet code, never load errors.
    /// </summary>
    private static List<LoadIssue> Issues(IEnumerable<WorkspaceDiagnostic> diagnostics, Solution solution)
    {
        var restore = solution.Projects
            .Where(p => p.FilePath is not null)
            .GroupBy(p => p.FilePath!, StringComparer.OrdinalIgnoreCase)
            .SelectMany(g => PackageInventory.RestoreMessages(g.Key, g))
            .Where(m => !m.IsError)
            .ToList();

        return [.. diagnostics
            .Select(d => d.Kind == WorkspaceDiagnosticKind.Failure && restore.FirstOrDefault(m => d.Message.EndsWith(m.Message, StringComparison.Ordinal)) is { } replayed
                ? new LoadIssue("warning", $"{replayed.Code} in {replayed.ProjectPath}: {replayed.Message}")
                : new LoadIssue(d.Kind == WorkspaceDiagnosticKind.Failure ? "error" : "warning", d.Message))
            .Distinct()];
    }

    /// <summary>
    /// SDK-style projects always load in the .NET SDK's build host, but some .NET Framework web projects in SDK format still
    /// import Visual Studio's targets through $(VSToolsPath) (WebApplications, for one), which only exist in a Visual
    /// Studio installation: point them there, as Visual Studio's own MSBuild does.
    /// </summary>
    private async Task<string?> VisualStudioToolsPathAsync(string directory, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || (await toolchain.ProbeAsync(directory, cancellationToken)).PreferredVisualStudio is not { } visualStudio)
        {
            return null;
        }

        var path = Path.Combine(visualStudio.InstallationPath, "MSBuild", "Microsoft", "VisualStudio", $"v{visualStudio.Version.Split('.')[0]}.0");
        return Directory.Exists(path) ? path : null;
    }

    /// <summary>
    /// An analyzer or source generator whose file does not exist (typically a generator project of the solution that was
    /// never built) carries no code, and Roslyn's reference and implementation searches fail on it: drop it, and say
    /// what to build to get its generated code.
    /// </summary>
    internal static (Solution Solution, List<LoadIssue> Issues) WithoutUnresolvedAnalyzers(Solution solution)
    {
        var missing = new SortedDictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in solution.Projects.ToList())
        {
            var unresolved = project.AnalyzerReferences.OfType<UnresolvedAnalyzerReference>().ToList();
            if (unresolved.Count == 0)
            {
                continue;
            }

            solution = solution.WithProjectAnalyzerReferences(project.Id, project.AnalyzerReferences.Where(r => r is not UnresolvedAnalyzerReference));
            foreach (var reference in unresolved)
            {
                var path = reference.FullPath ?? reference.Display;
                if (!missing.TryGetValue(path, out var projects))
                {
                    missing[path] = projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                projects.Add(project.FilePath ?? project.Name);
            }
        }

        return (solution, [.. missing.Select(kv =>
        {
            var producer = solution.Projects.FirstOrDefault(p => string.Equals(p.AssemblyName, Path.GetFileNameWithoutExtension(kv.Key), StringComparison.OrdinalIgnoreCase));
            var build = producer?.FilePath is { } file ? $"build {file} once, then reload" : "build the project that produces it, then reload";
            return new LoadIssue("warning", $"Analyzer or source generator not found: {kv.Key}. Code it generates is missing from {kv.Value.Count} project(s): {build}.");
        })]);
    }

    // Roslyn reads document text lazily, on first use. Read it all now, so the loaded solution is a true snapshot:
    // a file edited before anything looked at it must not leak into the load baseline that dotnet_check compares against.
    private static Task ReadAllTextAsync(Solution solution, CancellationToken cancellationToken) =>
        Parallel.ForEachAsync(
            solution.Projects.SelectMany(p => p.Documents),
            new ParallelOptions { CancellationToken = cancellationToken },
            async (document, token) => await document.GetTextAsync(token));

    [LoggerMessage(Level = LogLevel.Information, Message = "Loaded {Workspace}: {Projects} project file(s), {Documents} document(s), {Issues} issue(s) in {Elapsed}")]
    private static partial void LogLoaded(ILogger logger, string workspace, int projects, int documents, int issues, TimeSpan elapsed);

    // Progress<T> posts callbacks asynchronously; we need them applied before the load completes.
    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
