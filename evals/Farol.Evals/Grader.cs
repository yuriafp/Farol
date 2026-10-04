using System.Text;
using System.Text.RegularExpressions;

namespace Farol.Evals;

internal sealed record CheckOutcome(string Name, bool Passed, string Detail);

/// <summary>Decides a run with its task's checks, against the final reply and the workspace the agent left.</summary>
internal static class Grader
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);

    public static async Task<IReadOnlyList<CheckOutcome>> GradeAsync(EvalTask task, RunWorkspace workspace, string answer, string logPath, CancellationToken cancellationToken)
    {
        var outcomes = new List<CheckOutcome>();
        IReadOnlyList<string>? changed = null;
        var log = new StringBuilder();
        foreach (var check in task.Definition.Checks)
        {
            outcomes.Add(check.Type switch
            {
                "answer" => Patterns(check, answer, "the reply"),
                "file" => File(check, workspace.Directory),
                "changed" => Changed(check, changed ??= await Git.ChangedFilesAsync(workspace.Directory, cancellationToken)),
                "command" => await CommandAsync(check, workspace.Directory, task.Directory, log, cancellationToken),
                _ => new CheckOutcome(check.Label, false, $"unknown check type '{check.Type}'"),
            });
        }

        await System.IO.File.WriteAllTextAsync(logPath, log.ToString(), cancellationToken);
        return outcomes;
    }

    private static CheckOutcome Patterns(Check check, string text, string what)
    {
        var patterns = check.Patterns ?? [];
        var matched = patterns.Where(p => Regex.IsMatch(text, p, RegexOptions.IgnoreCase | RegexOptions.Multiline, RegexTimeout)).ToList();
        var missing = patterns.Except(matched).ToList();
        var (passed, detail) = (check.Match ?? "all") switch
        {
            "any" => (matched.Count > 0, matched.Count > 0 ? $"found {matched[0]}" : $"none of {patterns.Count} patterns in {what}"),
            "none" => (matched.Count == 0, matched.Count == 0 ? "absent, as required" : $"{what} contains {string.Join(", ", matched)}"),
            "min" => (matched.Count >= (check.Min ?? 1), $"{matched.Count} of {patterns.Count} found (at least {check.Min ?? 1} needed); missing: {string.Join(", ", missing)}"),
            _ => (missing.Count == 0, missing.Count == 0 ? $"all {patterns.Count} found" : $"missing from {what}: {string.Join(", ", missing)}"),
        };
        return new CheckOutcome(check.Label, passed, detail);
    }

    private static CheckOutcome File(Check check, string directory)
    {
        var path = Path.Combine(directory, check.Path ?? throw new InvalidDataException("A file check needs a path."));
        var exists = System.IO.File.Exists(path);
        if (check.Exists is false)
        {
            return new CheckOutcome(check.Label, !exists, exists ? $"{check.Path} still exists" : $"{check.Path} is gone");
        }

        if (!exists)
        {
            return new CheckOutcome(check.Label, false, $"{check.Path} does not exist");
        }

        return check.Patterns is null ? new CheckOutcome(check.Label, true, $"{check.Path} exists") : Patterns(check, System.IO.File.ReadAllText(path), check.Path);
    }

    private static CheckOutcome Changed(Check check, IReadOnlyList<string> changed)
    {
        var problems = new List<string>();
        foreach (var glob in check.Include ?? [])
        {
            if (!changed.Any(f => Glob(glob).IsMatch(f)))
            {
                problems.Add($"nothing changed under {glob}");
            }
        }

        if (check.Only is { } only)
        {
            problems.AddRange(changed.Where(f => !only.Any(g => Glob(g).IsMatch(f))).Select(f => $"{f} changed but should not have"));
        }

        foreach (var glob in check.Exclude ?? [])
        {
            problems.AddRange(changed.Where(f => Glob(glob).IsMatch(f)).Select(f => $"{f} changed but should not have"));
        }

        return new CheckOutcome(check.Label, problems.Count == 0, problems.Count == 0 ? $"{changed.Count} file(s) changed as expected" : string.Join("; ", problems.Take(8)));
    }

    // {task} is the task's own folder, for checks that need a script of their own.
    private static async Task<CheckOutcome> CommandAsync(Check check, string directory, string taskDirectory, StringBuilder log, CancellationToken cancellationToken)
    {
        var run = (check.Run ?? throw new InvalidDataException("A command check needs 'run'.")).Replace("{task}", taskDirectory, StringComparison.Ordinal);
        var result = await Shell.RunAsync(run, directory, TimeSpan.FromMinutes(check.TimeoutMinutes ?? 15), cancellationToken);
        log.Append("> ").AppendLine(run).AppendLine(result.Output).Append("[exit ").Append(result.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)).AppendLine("]").AppendLine();

        var expected = check.ExitCode ?? 0;
        if (result.ExitCode != expected)
        {
            return new CheckOutcome(check.Label, false, result.TimedOut ? "timed out" : $"exit code {result.ExitCode}, expected {expected}");
        }

        return check.Patterns is null ? new CheckOutcome(check.Label, true, $"exit code {expected}") : Patterns(check, result.Output, "the command output");
    }

    // ** spans directories, * and ? stay inside one; case-insensitive, whole path.
    private static Regex Glob(string glob) => new(
        "^" + Regex.Escape(glob.Replace('\\', '/')).Replace(@"\*\*/", "(.*/)?", StringComparison.Ordinal).Replace(@"\*\*", ".*", StringComparison.Ordinal)
            .Replace(@"\*", "[^/]*", StringComparison.Ordinal).Replace(@"\?", "[^/]", StringComparison.Ordinal) + "$",
        RegexOptions.IgnoreCase,
        RegexTimeout);
}
