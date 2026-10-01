using System.Collections.Concurrent;
using System.Diagnostics;
using Farol.Engine.Packages;
using Farol.Engine.Workspaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;

namespace Farol.Engine.Loading;

public sealed record LoadedWorkspace(MSBuildWorkspace Workspace, Solution Solution, LoadReport Report);

/// <summary>
/// Loads solutions through Roslyn's MSBuildWorkspace. MSBuild runs out of process in a BuildHost:
/// SDK-style projects use the .NET SDK host, and classic .NET Framework projects use the net472 host
/// with Visual Studio's MSBuild — which is what gives legacy projects full fidelity.
/// </summary>
public sealed partial class MSBuildWorkspaceLoader(ILogger<MSBuildWorkspaceLoader> logger)
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

        var workspace = MSBuildWorkspace.Create(new Dictionary<string, string>(properties, StringComparer.OrdinalIgnoreCase));
        try
        {
            var solution = target.Kind == WorkspaceTargetKind.Project
                ? (await workspace.OpenProjectAsync(target.Path, progress, cancellationToken)).Solution
                : await workspace.OpenSolutionAsync(target.Path, progress, cancellationToken);
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
                Issues: Issues(workspace.Diagnostics, solution));

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
