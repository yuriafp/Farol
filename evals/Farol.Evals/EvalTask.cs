namespace Farol.Evals;

/// <summary>
/// task.json: the job the task belongs to (spec 001's three jobs), the repository it runs in, its limits and the checks
/// that decide whether a run succeeded. The prompt is prompt.md; reference.md and reference.patch are a known-good
/// answer and change, which `validate` uses to prove the checks pass on a right answer and fail on none.
/// </summary>
internal sealed record TaskDefinition(string Job, string Repo, int MaxTurns, int TimeoutMinutes, IReadOnlyList<Check> Checks);

/// <summary>
/// One check of a run's outcome. answer: regexes over the final reply. file: regexes over a file. changed: globs over the
/// files changed since the baseline. command: a build or test command and its exit code. Regexes ignore case.
/// </summary>
internal sealed record Check(
    string Type,
    string? Name,
    IReadOnlyList<string>? Patterns,
    string? Match,
    int? Min,
    string? Path,
    bool? Exists,
    IReadOnlyList<string>? Include,
    IReadOnlyList<string>? Only,
    IReadOnlyList<string>? Exclude,
    string? Run,
    int? ExitCode,
    int? TimeoutMinutes)
{
    public string Label => Name ?? Type switch
    {
        "command" => Run ?? "command",
        "file" => $"file {Path}",
        _ => Type,
    };
}

internal sealed record EvalTask(string Id, string Directory, TaskDefinition Definition, string Prompt)
{
    public string? ReferenceAnswer => File.Exists(System.IO.Path.Combine(Directory, "reference.md")) ? File.ReadAllText(System.IO.Path.Combine(Directory, "reference.md")) : null;

    public string? ReferencePatch => File.Exists(System.IO.Path.Combine(Directory, "reference.patch")) ? System.IO.Path.Combine(Directory, "reference.patch") : null;

    public static IReadOnlyList<EvalTask> Load(string root, string? filter)
    {
        var tasks = new List<EvalTask>();
        foreach (var file in System.IO.Directory.EnumerateFiles(System.IO.Path.Combine(root, "evals", "tasks"), "task.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var directory = System.IO.Path.GetDirectoryName(file)!;
            var id = System.IO.Path.GetFileName(directory);
            if (filter is not null && !Matches(id, filter))
            {
                continue;
            }

            var definition = System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(file), EvalJson.Default.TaskDefinition)
                ?? throw new InvalidDataException($"{file} is empty.");
            tasks.Add(new EvalTask(id, directory, definition, File.ReadAllText(System.IO.Path.Combine(directory, "prompt.md")).Trim()));
        }

        return tasks;
    }

    /// <summary>Comma-separated prefixes or exact ids: "lm,mm01" picks every lm* task and mm01.</summary>
    private static bool Matches(string id, string filter) =>
        filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(f => id.StartsWith(f, StringComparison.OrdinalIgnoreCase));
}
