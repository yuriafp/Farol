using System.ComponentModel;
using System.Globalization;
using Farol.Core.Paths;
using Farol.Core.Text;
using Farol.Engine.Building;

using Farol.Tools.Infrastructure;

namespace Farol.Tools.Features.BuildAndTest;

/// <summary>Build results for agents: the verdict and toolchain first, then errors and warnings grouped by project.</summary>
internal static class BuildRenderer
{
    private const int MaxMessageLength = 300;

    public static BuildOutput Render(ResponseBuilder text, BuildResult build, string root)
    {
        text.Line($"build: {Verdict(build)} · {Counts(build)} · {Seconds(build.Elapsed)}");
        text.Line($"toolchain: {build.Toolchain} · {DisplayPath.From(root, build.Target)} · {build.Configuration}");
        return Output(build, root, Issues(text, build, root));
    }

    public static BuildOutput Output(BuildResult build, string root, (int Errors, int Warnings) shown) => new(
        build.TimedOut ? "timeout" : build.Succeeded ? "passed" : "failed",
        Verdict(build),
        build.Succeeded,
        build.ExitCode,
        build.TimedOut,
        build.Elapsed.TotalSeconds,
        build.Toolchain,
        DisplayPath.From(root, build.Target),
        build.Configuration,
        new Page<BuildIssueItem>(build.Errors.Count, 0, shown.Errors < build.Errors.Count ? shown.Errors : null, [.. build.Errors.Take(shown.Errors).Select(i => Item(i, root))]),
        new Page<BuildIssueItem>(build.Warnings.Count, 0, shown.Warnings < build.Warnings.Count ? shown.Warnings : null, [.. build.Warnings.Take(shown.Warnings).Select(i => Item(i, root))]),
        build.Succeeded || build.Errors.Count > 0 ? null : build.OutputTail);

    private static BuildIssueItem Item(BuildIssue issue, string root) => new(
        issue.FilePath is null ? null : issue.Line > 0 ? DisplayPath.Location(root, issue.FilePath, issue.Line) : DisplayPath.From(root, issue.FilePath),
        issue.IsError ? "error" : "warning",
        issue.Code,
        issue.Message.Length > MaxMessageLength ? string.Concat(issue.Message.AsSpan(0, MaxMessageLength), "…") : issue.Message,
        issue.Project,
        issue.TargetFrameworks);

    /// <summary>The errors (then warnings) of a build, or the end of its output when it failed without reporting any.</summary>
    public static (int Errors, int Warnings) Issues(ResponseBuilder text, BuildResult build, string root)
    {
        var shown = (Errors: 0, Warnings: 0);
        if (build.TimedOut)
        {
            text.Line("the build ran past Farol:BuildTimeoutMinutes and its whole process tree was stopped.");
        }

        if (build.Errors.Count > 0)
        {
            text.Line($"errors ({build.Errors.Count}):");
            shown.Errors = Entries(text, build.Errors, root);
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
            shown.Warnings = Entries(text, build.Warnings, root);
        }

        return shown;
    }

    public static string Counts(BuildResult build) =>
        $"{build.Errors.Count} error(s), {build.Warnings.Count} warning(s)";

    public static string Seconds(TimeSpan elapsed) =>
        elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";

    private static string Verdict(BuildResult build) =>
        build.TimedOut ? "stopped (timeout)" : build.Succeeded ? "succeeded" : $"failed (exit code {build.ExitCode})";

    private static int Entries(ResponseBuilder text, IReadOnlyList<BuildIssue> issues, string root)
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
        return written;
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

/// <summary>Structured output of dotnet_build; the issues are the ones the text lists.</summary>
public sealed record BuildOutput(
    [property: Description("passed, failed or timeout.")] string Status,
    string Verdict,
    bool Succeeded,
    int ExitCode,
    bool TimedOut,
    double Seconds,
    string Toolchain,
    string Target,
    string Configuration,
    Page<BuildIssueItem> Errors,
    Page<BuildIssueItem> Warnings,
    string? OutputTail);

public sealed record BuildIssueItem(string? Location, string Severity, string? Code, string Message, string Project, IReadOnlyList<string> TargetFrameworks);
