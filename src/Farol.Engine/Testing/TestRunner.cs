using Farol.Core.Execution;
using Farol.Core.Paths;
using Farol.Engine.Building;
using Farol.Engine.Toolchain;
using Farol.Engine.Workspaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farol.Engine.Testing;

public sealed record TestFailure(string Name, string Message, IReadOnlyList<string> UserFrames);

/// <summary>The outcome of one test project: counts and failures, or the build that failed first, or what went wrong.</summary>
public sealed record TestProjectRun(
    string Project,
    string Runner,
    int Total,
    int Passed,
    int Failed,
    int Skipped,
    IReadOnlyList<TestFailure> Failures,
    BuildResult? FailedBuild,
    string? Problem,
    string? Note,
    TimeSpan Elapsed);

/// <summary>
/// Builds each test project with the toolchain that works for it, then runs its tests with the matching host
/// (vstest.console for classic projects, dotnet test with VSTest or Microsoft.Testing.Platform for SDK-style ones)
/// and reads the results from TRX. Holds the workspace's build turn for the whole run.
/// </summary>
public sealed partial class TestRunner(BuildRunner builds, IProcessRunner runner, ToolchainProbe toolchain, IOptions<FarolEngineOptions> options, ILogger<TestRunner> logger)
{
    private const int MaxMessageLines = 12;
    private const int MaxMessageLength = 1200;
    private const int TailLines = 20;

    private TimeSpan Timeout => TimeSpan.FromMinutes(Math.Max(1, options.Value.TestTimeoutMinutes));

    public async Task<IReadOnlyList<TestProjectRun>> RunAsync(
        WorkspaceSnapshot snapshot,
        WorkspaceTarget target,
        IReadOnlyList<TestTarget> targets,
        string configuration,
        string root,
        Func<string, bool> isUserCode,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targets);
        using var turn = await builds.AcquireAsync(target, cancellationToken);
        var runs = new List<TestProjectRun>();
        foreach (var testTarget in targets)
        {
            var build = await builds.BuildCoreAsync(snapshot, target, testTarget.Project.FilePath, configuration, progress, cancellationToken);
            if (!build.Succeeded)
            {
                runs.Add(new TestProjectRun(testTarget.Project.Name, build.Toolchain, 0, 0, 0, 0, [], build, null, null, build.Elapsed));
                continue;
            }

            progress?.Report($"running tests in {testTarget.Project.Name}");
            runs.Add(await RunProjectAsync(snapshot, target, testTarget, build, configuration, root, isUserCode, cancellationToken));
        }

        return runs;
    }

    private async Task<TestProjectRun> RunProjectAsync(
        WorkspaceSnapshot snapshot,
        WorkspaceTarget target,
        TestTarget testTarget,
        BuildResult build,
        string configuration,
        string root,
        Func<string, bool> isUserCode,
        CancellationToken cancellationToken)
    {
        var project = testTarget.Project;
        var host = TestCommands.HostFor(project);
        var resultsDirectory = ArtifactDirectory.For("tests", target.Path);
        var trxFileName = project.Name + ".trx";
        var trxPath = Path.Combine(resultsDirectory, trxFileName);
        if (File.Exists(trxPath))
        {
            File.Delete(trxPath);
        }

        string fileName, subject, runnerName;
        if (host == TestHost.VSTestConsole)
        {
            var visualStudio = (await toolchain.ProbeAsync(target.Directory, cancellationToken)).PreferredVisualStudio;
            if (visualStudio?.VSTestPath is not { } vstest)
            {
                return Problem(project, "vstest.console", "Running classic .NET Framework tests needs vstest.console.exe from Visual Studio's testing tools, and it was not found.");
            }

            var outputs = build.Outputs.TryGetValue(project.Name, out var built) ? built : [];
            var assembly = outputs.FirstOrDefault(o => o.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                ?? snapshot.Solution.GetProject(project.Variants[0])?.OutputFilePath;
            if (assembly is null || !File.Exists(assembly))
            {
                return Problem(project, "vstest.console", $"The build did not report the test assembly of {project.Name}.");
            }

            (fileName, subject, runnerName) = (vstest, assembly, $"vstest.console ({visualStudio.DisplayName} {visualStudio.ShortVersion})");
        }
        else
        {
            (fileName, subject, runnerName) = ("dotnet", project.FilePath, host == TestHost.DotnetTestPlatform ? "dotnet test (Microsoft.Testing.Platform)" : "dotnet test (VSTest)");
        }

        LogRunning(logger, project.Name, runnerName);
        var arguments = TestCommands.Arguments(host, testTarget, subject, configuration, resultsDirectory, trxFileName);
        var run = await runner.RunAsync(new ProcessSpec(fileName, arguments, Path.GetDirectoryName(project.FilePath)!, Timeout), cancellationToken);
        var note = host == TestHost.DotnetTestPlatform && project.Frameworks.Contains(TestFrameworks.TUnit) && TestCommands.Filter(testTarget) is not null
            ? "TUnit takes no VSTest filter: every test in the project ran."
            : null;

        if (TrxReader.Read(trxPath) is not { } results)
        {
            var problem = run.TimedOut
                ? $"Stopped after {Timeout.TotalMinutes:0} min (Farol:TestTimeoutMinutes); the whole process tree was killed."
                : $"{runnerName} exited with code {run.ExitCode} and wrote no results:\n{Tail(run.StandardOutput + run.StandardError)}";
            return new TestProjectRun(project.Name, runnerName, 0, 0, 0, 0, [], null, problem, note, run.Elapsed);
        }

        var failures = results
            .Where(r => r.Failed)
            .Select(r => new TestFailure(r.Name, Message(r.Message), StackFrames.InUserCode(r.StackTrace, root, isUserCode)))
            .ToList();
        var passed = results.Count(r => r.Passed);
        return new TestProjectRun(
            project.Name,
            runnerName,
            results.Count,
            passed,
            failures.Count,
            results.Count - passed - failures.Count,
            failures,
            null,
            run.TimedOut ? $"Stopped after {Timeout.TotalMinutes:0} min (Farol:TestTimeoutMinutes): results are partial." : null,
            note,
            run.Elapsed);
    }

    private static TestProjectRun Problem(TestProjectInfo project, string runner, string problem) =>
        new(project.Name, runner, 0, 0, 0, 0, [], null, problem, null, TimeSpan.Zero);

    private static string Message(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "(no message)";
        }

        var lines = message.Trim().Split('\n').Select(l => l.TrimEnd('\r')).Take(MaxMessageLines);
        var text = string.Join('\n', lines);
        return text.Length > MaxMessageLength ? string.Concat(text.AsSpan(0, MaxMessageLength), "…") : text;
    }

    private static string Tail(string output) =>
        string.Join('\n', output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(TailLines));

    [LoggerMessage(Level = LogLevel.Information, Message = "Running tests of {Project} with {Runner}")]
    private static partial void LogRunning(ILogger logger, string project, string runner);
}
