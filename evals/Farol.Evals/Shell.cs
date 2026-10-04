using System.Diagnostics;
using System.Text;

namespace Farol.Evals;

/// <summary>Runs command lines from task and repository files: an executable and its arguments, double quotes allowed.</summary>
internal static class Shell
{
    /// <summary>Visual Studio's MSBuild, found with vswhere; null without Visual Studio.</summary>
    public static readonly Lazy<string?> MSBuild = new(() => VsWhere("MSBuild\\**\\Bin\\MSBuild.exe"));

    public static readonly Lazy<string?> VSTest = new(() => VsWhere("Common7\\IDE\\Extensions\\TestPlatform\\vstest.console.exe"));

    public static async Task<CommandResult> RunAsync(string commandLine, string directory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var parts = Split(Expand(commandLine));
        var start = new ProcessStartInfo(parts[0])
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in parts.Skip(1))
        {
            start.ArgumentList.Add(argument);
        }

        // Builds and tests print in English whatever the machine's language, so patterns in task files hold.
        start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        start.Environment["VSLANG"] = "1033";
        start.Environment["NuGetAudit"] = "false";

        var output = new StringBuilder();
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => Append(output, e.Data);
        process.ErrorDataReceived += (_, e) => Append(output, e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            return new CommandResult(-1, output + $"\n[timed out after {timeout.TotalMinutes:N0} min]", TimedOut: true);
        }

        return new CommandResult(process.ExitCode, output.ToString(), TimedOut: false);
    }

    public static string Expand(string commandLine)
    {
        if (commandLine.Contains("{msbuild}", StringComparison.Ordinal))
        {
            commandLine = commandLine.Replace("{msbuild}", Quote(MSBuild.Value ?? throw new InvalidOperationException("This command needs Visual Studio's MSBuild.")), StringComparison.Ordinal);
        }

        if (commandLine.Contains("{vstest}", StringComparison.Ordinal))
        {
            commandLine = commandLine.Replace("{vstest}", Quote(VSTest.Value ?? throw new InvalidOperationException("This command needs vstest.console.")), StringComparison.Ordinal);
        }

        return commandLine;
    }

    public static List<string> Split(string commandLine)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts;
    }

    private static string Quote(string path) => $"\"{path}\"";

    private static void Append(StringBuilder output, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (output)
        {
            output.AppendLine(line);
        }
    }

    private static string? VsWhere(string find)
    {
        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!OperatingSystem.IsWindows() || !File.Exists(vswhere))
        {
            return null;
        }

        var start = new ProcessStartInfo(vswhere) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var argument in new[] { "-latest", "-prerelease", "-requires", "Microsoft.Component.MSBuild", "-find", find })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var first = process.StandardOutput.ReadLine();
        process.WaitForExit();
        return string.IsNullOrWhiteSpace(first) ? null : first.Trim();
    }
}

internal sealed record CommandResult(int ExitCode, string Output, bool TimedOut);
