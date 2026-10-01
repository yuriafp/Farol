using System.Globalization;
using Farol.Core.Paths;
using Farol.Core.Text;
using Farol.Engine.Building;

namespace Farol.Tools.Features.BuildAndTest;

/// <summary>Build results for agents: the verdict and toolchain first, then errors and warnings grouped by project.</summary>
internal static class BuildRenderer
{
    private const int MaxMessageLength = 300;

    public static void Render(ResponseBuilder text, BuildResult build, string root)
    {
        text.Line($"build: {Verdict(build)} · {Counts(build)} · {Seconds(build.Elapsed)}");
        text.Line($"toolchain: {build.Toolchain} · {DisplayPath.From(root, build.Target)} · {build.Configuration}");
        Issues(text, build, root);
    }

    /// <summary>The errors (then warnings) of a build, or the end of its output when it failed without reporting any.</summary>
    public static void Issues(ResponseBuilder text, BuildResult build, string root)
    {
        if (build.TimedOut)
        {
            text.Line("the build ran past Farol:BuildTimeoutMinutes and its whole process tree was stopped.");
        }

        if (build.Errors.Count > 0)
        {
            text.Line($"errors ({build.Errors.Count}):");
            Entries(text, build.Errors, root);
        }
        else if (!build.Succeeded && build.OutputTail is { } tail)
        {
            text.Line("no error was reported; the output ended with:");
            foreach (var line in tail.Split('\n'))
            {
                if (!text.TryLine("  " + line))
                {
                    break;
                }
            }
        }

        if (build.Warnings.Count > 0)
        {
            text.Line($"warnings ({build.Warnings.Count}):");
            Entries(text, build.Warnings, root);
        }
    }

    public static string Counts(BuildResult build) =>
        $"{build.Errors.Count} error(s), {build.Warnings.Count} warning(s)";

    public static string Seconds(TimeSpan elapsed) =>
        elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";

    private static string Verdict(BuildResult build) =>
        build.TimedOut ? "stopped (timeout)" : build.Succeeded ? "succeeded" : $"failed (exit code {build.ExitCode})";

    private static void Entries(ResponseBuilder text, IReadOnlyList<BuildIssue> issues, string root)
    {
        string? project = null;
        var written = 0;
        foreach (var issue in issues)
        {
            if (!string.Equals(project, issue.Project, StringComparison.Ordinal))
            {
                if (!text.TryLine($"{issue.Project}:"))
                {
                    break;
                }

                project = issue.Project;
            }

            if (!text.TryLine(Line(issue, root)))
            {
                break;
            }

            written++;
        }

        text.More(issues.Count - written, "raise maxTokens to see them");
    }

    private static string Line(BuildIssue issue, string root)
    {
        var where = issue.FilePath is null ? "(project)" : issue.Line > 0 ? DisplayPath.Location(root, issue.FilePath, issue.Line) : DisplayPath.From(root, issue.FilePath);
        var message = issue.Message.Length > MaxMessageLength ? string.Concat(issue.Message.AsSpan(0, MaxMessageLength), "…") : issue.Message;
        var code = issue.Code is null ? string.Empty : " " + issue.Code;
        var frameworks = issue.TargetFrameworks.Count > 0 ? $" · {string.Join(", ", issue.TargetFrameworks)}" : string.Empty;
        return $"- {where} · {(issue.IsError ? "error" : "warning")}{code} · {message}{frameworks}";
    }
}
