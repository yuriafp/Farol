using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Farol.Evals;

internal static class Suite
{
    public static async Task<int> RunAsync(string root, string home, Dictionary<string, string> options, CancellationToken cancellationToken)
    {
        var tasks = EvalTask.Load(root, options.GetValueOrDefault("tasks"));
        using var repositories = new Repositories(root, home);
        var arms = (options.GetValueOrDefault("arms") ?? "farol,baseline").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var runs = int.Parse(options.GetValueOrDefault("runs") ?? "3", CultureInfo.InvariantCulture);
        var model = options.GetValueOrDefault("model") ?? "claude-sonnet-5-5";
        var concurrency = int.Parse(options.GetValueOrDefault("concurrency") ?? "2", CultureInfo.InvariantCulture);
        var maxCost = options.GetValueOrDefault("max-cost-usd") is { } limit ? double.Parse(limit, CultureInfo.InvariantCulture) : double.PositiveInfinity;
        var keep = options.GetValueOrDefault("keep-workspaces") == "true";
        var output = Path.GetFullPath(options.GetValueOrDefault("out") ?? Path.Combine(root, "artifacts", "evals", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)));
        Directory.CreateDirectory(output);
        var plugin = arms.Contains("farol") ? FarolPlugin.Prepare(root, output) : null;

        // Arms alternate within each round, so time of day and load treat both alike; finished runs are skipped (resume).
        var planned = (from round in Enumerable.Range(1, runs) from task in tasks from arm in arms select (Task: task, Arm: arm, Round: round))
            .Where(p => !File.Exists(Path.Combine(output, p.Task.Id, $"{p.Arm}-{p.Round}", "run.json")))
            .ToList();
        var spent = Summary.Load(output).Sum(r => r.CostUsd);
        Console.WriteLine($"{planned.Count} run(s) to go in {output}; model {model}; {concurrency} at a time.");

        using var gate = new SemaphoreSlim(concurrency);
        var running = new List<Task>();
        string? stopped = null;
        foreach (var (task, arm, round) in planned)
        {
            await gate.WaitAsync(cancellationToken);
            stopped ??= Volatile.Read(ref spent) >= maxCost ? $"cost ceiling of ${maxCost} reached" : null;
            if (stopped is not null)
            {
                gate.Release();
                break;
            }

            running.Add(Task.Run(
                async () =>
                {
                    try
                    {
                        var record = await ExecuteAsync(repositories, task, arm, round, model, plugin, output, keep, cancellationToken);
                        InterlockedAdd(ref spent, record.CostUsd);
                        if (record.RateLimited)
                        {
                            stopped ??= "the account's usage limit was reached: wait for it to reset, then run again with the same --out";
                        }
                    }
                    finally
                    {
                        gate.Release();
                    }
                },
                CancellationToken.None));
        }

        await Task.WhenAll(running);
        if (stopped is not null)
        {
            Console.Error.WriteLine($"Stopped early: {stopped}.");
        }

        var (markdown, met) = Summary.Write(output, Summary.Load(output), ClaudeVersion());
        Console.WriteLine();
        Console.WriteLine(markdown);
        return stopped is not null ? 2 : met ? 0 : 1;
    }

    public static async Task<int> ValidateAsync(string root, string home, Dictionary<string, string> options, CancellationToken cancellationToken)
    {
        var tasks = EvalTask.Load(root, options.GetValueOrDefault("tasks"));
        using var repositories = new Repositories(root, home);
        var log = Path.Combine(Path.GetTempPath(), "farol-evals-validate.txt");
        var broken = 0;
        foreach (var task in tasks)
        {
            var workspace = await repositories.CreateAsync(task.Definition.Repo, cancellationToken);
            try
            {
                var untouched = await Grader.GradeAsync(task, workspace, string.Empty, log, cancellationToken);
                if (task.ReferencePatch is { } patch)
                {
                    await Git.RunAsync(workspace.Directory, cancellationToken, "apply", "--whitespace=nowarn", patch);
                }

                var reference = await Grader.GradeAsync(task, workspace, task.ReferenceAnswer ?? string.Empty, log, cancellationToken);
                var failsUntouched = untouched.Any(o => !o.Passed);
                var passesReference = reference.All(o => o.Passed);
                broken += failsUntouched && passesReference ? 0 : 1;
                Console.WriteLine($"{(failsUntouched && passesReference ? "ok  " : "BAD ")} {task.Id}");
                foreach (var outcome in reference.Where(o => !o.Passed))
                {
                    Console.WriteLine($"      reference fails {outcome.Name}: {outcome.Detail}");
                }

                if (!failsUntouched)
                {
                    Console.WriteLine("      every check passes without doing anything");
                }
            }
            finally
            {
                await workspace.DisposeAsync(CancellationToken.None);
            }
        }

        Console.WriteLine(broken == 0 ? $"All {tasks.Count} task(s) valid." : $"{broken} task(s) need attention.");
        return broken == 0 ? 0 : 1;
    }

    public static int Report(string output)
    {
        var (markdown, met) = Summary.Write(output, Summary.Load(output), ClaudeVersion());
        Console.WriteLine(markdown);
        return met ? 0 : 1;
    }

    private static async Task<RunRecord> ExecuteAsync(
        Repositories repositories, EvalTask task, string arm, int round, string model, string? plugin, string output, bool keep, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(output, task.Id, $"{arm}-{round}");
        Directory.CreateDirectory(directory);
        var started = DateTimeOffset.UtcNow;
        RunRecord record;
        RunWorkspace? workspace = null;
        try
        {
            workspace = await repositories.CreateAsync(task.Definition.Repo, cancellationToken);
            var outcome = await AgentRunner.RunAsync(
                task, workspace.Directory, arm == "farol" ? plugin : null, model, Path.Combine(directory, "transcript.jsonl"), Path.Combine(directory, "stderr.txt"), cancellationToken);
            var checks = outcome.Exit == "cancelled" ? [] : await Grader.GradeAsync(task, workspace, outcome.Answer, Path.Combine(directory, "grading.txt"), cancellationToken);
            record = new RunRecord(
                task.Id, task.Definition.Job, task.Definition.Repo, arm, round, model, started.ToString("O", CultureInfo.InvariantCulture), outcome.Duration.TotalSeconds,
                outcome.Exit, outcome.Subtype, outcome.Turns, outcome.CostUsd, outcome.Tokens, outcome.ToolCalls, outcome.FarolCalls,
                outcome.IsolationProblem, outcome.RateLimited, checks, checks.Count > 0 && checks.All(c => c.Passed), outcome.Answer, Error: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            record = new RunRecord(
                task.Id, task.Definition.Job, task.Definition.Repo, arm, round, model, started.ToString("O", CultureInfo.InvariantCulture), (DateTimeOffset.UtcNow - started).TotalSeconds,
                "error", null, 0, 0, TokenCount.Zero, new Dictionary<string, int>(), 0, null, false, [], false, string.Empty, ex.Message);
        }
        finally
        {
            if (workspace is not null && !keep)
            {
                await workspace.DisposeAsync(CancellationToken.None);
            }
        }

        await File.WriteAllTextAsync(Path.Combine(directory, "run.json"), JsonSerializer.Serialize(record, RunJson.Default.RunRecord), CancellationToken.None);
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{task.Id,-28} {arm,-8} #{round}  {(record.Success ? "pass" : "FAIL")}  {record.Tokens.Total / 1000.0,7:N0}k tokens  ${record.CostUsd,5:N2}  {record.DurationSeconds / 60,4:N1} min  {record.FarolCalls,2} farol calls  {record.Error ?? record.IsolationProblem ?? (record.Exit == "completed" ? string.Empty : record.Exit)}"));
        return record;
    }

    private static void InterlockedAdd(ref double target, double value)
    {
        double current, updated;
        do
        {
            current = Volatile.Read(ref target);
            updated = current + value;
        }
        while (Interlocked.CompareExchange(ref target, updated, current) != current);
    }

    private static string ClaudeVersion()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("claude", "--version") { RedirectStandardOutput = true, UseShellExecute = false })!;
            var version = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return version.Split(' ')[0];
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "?";
        }
    }
}

/// <summary>The Farol arm's plugin: the repository's plugin, its MCP server pointed at a copy of the host built here.</summary>
internal static class FarolPlugin
{
    public static string Prepare(string root, string output)
    {
        var host = Path.Combine(root, "src", "Farol.Host", "bin", Arguments.Configuration, "net10.0");
        if (!File.Exists(Path.Combine(host, "Farol.Host.dll")))
        {
            throw new InvalidOperationException($"No Farol host in {host}: build the solution first.");
        }

        // A copy, so rebuilding Farol during a suite never pulls the server from under a run.
        var server = Path.Combine(output, "farol");
        CopyDirectory(host, server);
        var plugin = Path.Combine(output, "plugin");
        CopyDirectory(Path.Combine(root, "plugins", "farol"), plugin);
        var mcp = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["farol"] = new JsonObject
                {
                    ["type"] = "stdio",
                    ["command"] = "dotnet",
                    ["args"] = new JsonArray(Path.Combine(server, "Farol.Host.dll")),
                },
            },
        };
        File.WriteAllText(Path.Combine(plugin, ".mcp.json"), mcp.ToJsonString());
        return plugin;
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}

internal static class Arguments
{
    public static Dictionary<string, string> Parse(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            options[args[i].TrimStart('-')] = args[i + 1];
        }

        return options;
    }

    // bin/<configuration>/net10.0/ of this tool: run the host built in the same configuration.
    public static string Configuration =>
        new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Parent!.Name;

    public static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Farol.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Run Farol.Evals from a build inside the Farol repository.");
    }

    public static int Usage()
    {
        Console.Error.WriteLine("Usage: Farol.Evals run|validate [--tasks <ids>] [--arms farol,baseline] [--runs 3] [--model <model>] [--concurrency 2] [--out <dir>] [--max-cost-usd <usd>]");
        Console.Error.WriteLine("       Farol.Evals report --out <dir>");
        return 2;
    }
}
