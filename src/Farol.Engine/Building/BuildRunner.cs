using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Farol.Core;
using Farol.Core.Execution;
using Farol.Core.Paths;
using Farol.Engine.Toolchain;
using Farol.Engine.Workspaces;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farol.Engine.Building;

/// <summary>
/// Builds with the toolchain that works for the code: <c>dotnet build</c> for SDK-style projects, Visual Studio's
/// MSBuild (with packages.config restore) as soon as a classic .NET Framework project is involved. Builds and test
/// runs of one workspace never overlap, and every build writes the binary log its issues are read from.
/// </summary>
public sealed partial class BuildRunner(IProcessRunner runner, ToolchainProbe toolchain, IOptions<FarolEngineOptions> options, ILogger<BuildRunner> logger)
{
    private const int TailLines = 25;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    private TimeSpan Timeout => TimeSpan.FromMinutes(Math.Max(1, options.Value.BuildTimeoutMinutes));

    /// <summary>Builds the whole workspace, or one project file and the projects it references.</summary>
    public async Task<BuildResult> BuildAsync(
        WorkspaceSnapshot snapshot, WorkspaceTarget target, string? projectFile, string configuration, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        using var turn = await AcquireAsync(target, progress, cancellationToken);
        return await BuildCoreAsync(snapshot, target, projectFile, configuration, progress, cancellationToken);
    }

    /// <summary>
    /// Two MSBuild processes writing the same obj/ folders fail: one build or test run per workspace at a time, across
    /// Farol processes too, as when two clients work on the same repository.
    /// </summary>
    internal async Task<IDisposable> AcquireAsync(WorkspaceTarget target, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var gate = _gates.GetOrAdd(target.Path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return new Turn(gate, await HoldTurnFileAsync(target, progress, cancellationToken));
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    /// <summary>The file a Farol process holds open while it builds or tests the workspace.</summary>
    public static string TurnFile(string workspacePath) => Path.Combine(ArtifactDirectory.For("builds", workspacePath), "turn.lock");

    // The system lets the file go when its holder exits, crashed or not, so a turn is never left taken. The other process
    // holds it for at most a build and a test run, each bounded by its timeout; progress repeats so clients keep waiting.
    private async Task<FileStream> HoldTurnFileAsync(WorkspaceTarget target, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var path = TurnFile(target.Path);
        var limit = TimeSpan.FromMinutes(Math.Max(1, options.Value.BuildTimeoutMinutes) + Math.Max(1, options.Value.TestTimeoutMinutes));
        var waited = System.Diagnostics.Stopwatch.StartNew();
        var reported = TimeSpan.MinValue;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when (IsHeldByAnother(ex) && File.Exists(path))
            {
                if (waited.Elapsed > limit)
                {
                    throw new FarolException(
                        ErrorCodes.Conflict,
                        $"Another Farol process has been building or testing {target.DisplayName} for {(int)limit.TotalMinutes} minutes.",
                        "Wait for that client's build or test run to end, or stop it, then try again.");
                }

                if (reported == TimeSpan.MinValue || waited.Elapsed - reported >= TimeSpan.FromSeconds(15))
                {
                    if (reported == TimeSpan.MinValue)
                    {
                        LogWaiting(logger, target.Path);
                    }

                    reported = waited.Elapsed;
                    progress?.Report($"waiting for another Farol process that is building or testing this workspace ({(int)waited.Elapsed.TotalSeconds} s)");
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
    }

    // Windows says which IOException means the file is open elsewhere (sharing or lock violation); elsewhere any one does.
    private static bool IsHeldByAnother(IOException ex) => !OperatingSystem.IsWindows() || (ex.HResult & 0xFFFF) is 32 or 33;

    /// <summary>Builds without taking the workspace's turn: the caller holds it.</summary>
    internal async Task<BuildResult> BuildCoreAsync(
        WorkspaceSnapshot snapshot, WorkspaceTarget target, string? projectFile, string configuration, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var command = await PlanAsync(snapshot, target, projectFile, configuration, cancellationToken);
        if (File.Exists(command.BinaryLog))
        {
            File.Delete(command.BinaryLog);
        }

        progress?.Report($"building {Path.GetFileName(command.Path)} with {command.Toolchain}");
        LogBuilding(logger, command.Path, command.Toolchain);
        var spec = new ProcessSpec(command.FileName, command.Arguments, command.WorkingDirectory, Timeout)
        {
            OnOutputLine = line =>
            {
                if (BuiltProject().Match(line) is { Success: true } match)
                {
                    progress?.Report($"built {match.Groups["project"].Value}");
                }
            },
        };

        var run = await runner.RunAsync(spec, cancellationToken);
        LogBuilt(logger, command.Path, run.ExitCode, command.BinaryLog);
        var log = BuildLogReader.ReadBinaryLog(command.BinaryLog) ?? BuildLogReader.ReadConsoleOutput(run.StandardOutput);
        var (errors, warnings) = log.Issues();
        var succeeded = run.ExitCode == 0 && !run.TimedOut;
        return new BuildResult(
            succeeded,
            run.ExitCode,
            run.TimedOut,
            run.Elapsed,
            command.Toolchain,
            command.Path,
            configuration,
            errors,
            warnings,
            log.Outputs.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.OrdinalIgnoreCase),
            File.Exists(command.BinaryLog) ? command.BinaryLog : null,
            succeeded || errors.Count > 0 ? null : Tail(run.StandardOutput + run.StandardError));
    }

    private async Task<BuildCommand> PlanAsync(WorkspaceSnapshot snapshot, WorkspaceTarget target, string? projectFile, string configuration, CancellationToken cancellationToken)
    {
        var path = projectFile ?? target.Path;
        var scope = Scope(snapshot, projectFile);
        var binaryLog = Path.Combine(ArtifactDirectory.For("builds", target.Path), Path.GetFileNameWithoutExtension(path) + ".binlog");
        string[] common = ["-nologo", "-v:m", "-clp:NoSummary", $"-bl:{binaryLog}", "-p:PreferredUILang=en-US"];
        var directory = Path.GetDirectoryName(path)!;

        // VS's MSBuild builds SDK-style projects too, but only it builds classic web, WPF and resx-heavy ones.
        if (scope.All(p => p.IsSdkStyle))
        {
            var sdk = (await toolchain.ProbeAsync(directory, cancellationToken)).DotnetSdkVersion;
            return new BuildCommand("dotnet", ["build", path, "-c", configuration, .. common], directory, $"dotnet build (.NET SDK {sdk ?? "not found"})", path, binaryLog);
        }

        if ((await toolchain.ProbeAsync(target.Directory, cancellationToken)).PreferredVisualStudio is not { MSBuildPath: { } msbuild } visualStudio)
        {
            throw new FarolException(
                ErrorCodes.InvalidArgument,
                "Building classic .NET Framework projects needs the MSBuild of Visual Studio or Build Tools, and none was found.",
                "Install Visual Studio Build Tools with the .NET desktop and web build workloads, or build these projects on a machine that has them.");
        }

        var arguments = new List<string> { path, "-restore", $"-p:Configuration={configuration}", "-m", "-nodeReuse:false" };
        arguments.AddRange(common);
        if (scope.Any(p => File.Exists(Path.Combine(Path.GetDirectoryName(p.FilePath)!, "packages.config"))))
        {
            arguments.Add("-p:RestorePackagesConfig=true");
        }

        // packages.config restores into the solution's packages folder; a project built alone must be told where it is.
        if (projectFile is not null && target.Kind != WorkspaceTargetKind.Project)
        {
            arguments.Add($"-p:SolutionDir={target.Directory}{Path.DirectorySeparatorChar}");
        }

        return new BuildCommand(msbuild, arguments, target.Directory, $"MSBuild {visualStudio.ShortVersion} ({visualStudio.DisplayName})", path, binaryLog);
    }

    // The projects a build touches: everything, or the project and what it references.
    private static List<ProjectEntry> Scope(WorkspaceSnapshot snapshot, string? projectFile)
    {
        var solution = snapshot.Solution;
        if (projectFile is null)
        {
            return [.. solution.ProjectIds.Select(snapshot.Projects.Get).OfType<ProjectEntry>()];
        }

        var graph = solution.GetProjectDependencyGraph();
        var roots = solution.Projects.Where(p => string.Equals(p.FilePath, projectFile, StringComparison.OrdinalIgnoreCase)).Select(p => p.Id).ToList();
        return [.. roots.Concat(roots.SelectMany(graph.GetProjectsThatThisProjectTransitivelyDependsOn)).Distinct().Select(snapshot.Projects.Get).OfType<ProjectEntry>()];
    }

    private static string Tail(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join('\n', lines.TakeLast(TailLines));
    }

    [GeneratedRegex(@"^\s*(?<project>[^\s>]+) -> ")]
    private static partial Regex BuiltProject();

    [LoggerMessage(Level = LogLevel.Information, Message = "Building {Path} with {Toolchain}")]
    private static partial void LogBuilding(ILogger logger, string path, string toolchain);

    // The binary log stays out of tool responses (its path is outside the workspace); people find it here.
    [LoggerMessage(Level = LogLevel.Information, Message = "Built {Path} (exit code {ExitCode}); binary log: {BinaryLog}")]
    private static partial void LogBuilt(ILogger logger, string path, int exitCode, string binaryLog);

    private sealed record BuildCommand(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, string Toolchain, string Path, string BinaryLog);

    [LoggerMessage(Level = LogLevel.Information, Message = "Waiting for another Farol process that is building or testing {Path}")]
    private static partial void LogWaiting(ILogger logger, string path);

    private sealed class Turn(SemaphoreSlim gate, FileStream turnFile) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                try
                {
                    turnFile.Dispose();
                }
                finally
                {
                    gate.Release();
                }
            }
        }
    }
}
