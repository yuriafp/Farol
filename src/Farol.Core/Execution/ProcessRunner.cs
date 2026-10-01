using System.Diagnostics;
using System.Text;

namespace Farol.Core.Execution;

public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, TimeSpan Timeout)
{
    /// <summary>Extra environment variables for the child process.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>Receives each line of standard output as it arrives, e.g. to report progress. Must not throw.</summary>
    public Action<string>? OnOutputLine { get; init; }
}

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, TimeSpan Elapsed, bool TimedOut);

/// <summary>
/// Runs external tools (dotnet, msbuild, vswhere, vstest). The local implementation starts child
/// processes; a server host can swap in a sandboxed runner without touching the callers.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken);
}

public sealed class LocalProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var startInfo = new ProcessStartInfo(spec.FileName)
        {
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Under stdio our stdin/stdout carry MCP traffic: children must never inherit either.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in spec.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // MSBuild node reuse leaves processes behind that lock files in the user's repository.
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";

        // Tool output reaches agents in English whatever the machine's language (dotnet CLI, MSBuild.exe).
        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        startInfo.Environment["VSLANG"] = "1033";
        foreach (var (name, value) in spec.Environment ?? new Dictionary<string, string>())
        {
            startInfo.Environment[name] = value;
        }

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutGate = new Lock();
        var stderrGate = new Lock();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stdoutGate)
                {
                    stdout.AppendLine(e.Data);
                }

                spec.OnOutputLine?.Invoke(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stderrGate)
                {
                    stderr.AppendLine(e.Data);
                }
            }
        };

        var stopwatch = Stopwatch.StartNew();
        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(spec.Timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            timedOut = true;
        }

        lock (stdoutGate)
        {
            lock (stderrGate)
            {
                return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString(), stopwatch.Elapsed, timedOut);
            }
        }
    }
}
