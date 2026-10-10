using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Farol.Evals;

/// <summary>Tokens of every model call of a run, subagents included: what "tokens per task" counts.</summary>
internal sealed record TokenCount(long Input, long Output, long CacheRead, long CacheCreation)
{
    public static readonly TokenCount Zero = new(0, 0, 0, 0);

    public long Total => Input + Output + CacheRead + CacheCreation;

    public TokenCount Add(TokenCount other) => new(Input + other.Input, Output + other.Output, CacheRead + other.CacheRead, CacheCreation + other.CacheCreation);
}

internal sealed record AgentOutcome(
    string Exit,
    string? Subtype,
    int Turns,
    double CostUsd,
    TokenCount Tokens,
    IReadOnlyDictionary<string, int> ToolCalls,
    string Answer,
    string? IsolationProblem,
    bool RateLimited,
    TimeSpan Duration)
{
    public int FarolCalls => ToolCalls.Where(kv => kv.Key.StartsWith(AgentRunner.FarolToolPrefix, StringComparison.Ordinal)).Sum(kv => kv.Value);
}

/// <summary>
/// One headless Claude Code session (claude -p) in a run's workspace. Both arms get the same built-in tools, and nothing
/// of the machine's own configuration: no user settings or instructions, plugins, MCP servers, claude.ai connectors or
/// auto-memory. The Farol arm adds the plugin (MCP server, skill, hook) and nothing else; the session's init message
/// proves it.
/// </summary>
internal static class AgentRunner
{
    public const string FarolToolPrefix = "mcp__plugin_farol_farol__";

    private static readonly string[] Tools =
        ["Task", "Bash", "PowerShell", "Read", "Edit", "Write", "Glob", "Grep", "NotebookEdit", "Skill", "ToolSearch", "TaskCreate", "TaskGet", "TaskList", "TaskUpdate", "TaskStop"];

    public static async Task<AgentOutcome> RunAsync(
        EvalTask task, string workspace, string? plugin, string model, string transcriptPath, string stderrPath, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("claude")
        {
            WorkingDirectory = workspace,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        string[] arguments =
        [
            "-p", task.Prompt,
            "--output-format", "stream-json", "--verbose",
            "--model", model,
            "--max-turns", task.Definition.MaxTurns.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--setting-sources", "project,local",
            "--settings", UserInstructionsExcluded(),
            "--no-session-persistence",
            "--permission-mode", "bypassPermissions",
            "--tools", string.Join(',', Tools),
            .. plugin is null ? Array.Empty<string>() : ["--plugin-dir", plugin],
        ];
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["CLAUDE_CODE_DISABLE_AUTO_MEMORY"] = "1";
        start.Environment["ENABLE_CLAUDEAI_MCP_SERVERS"] = "false";
        // Builds of large legacy solutions take minutes; neither arm should lose one to a tool timeout.
        start.Environment["BASH_DEFAULT_TIMEOUT_MS"] = "600000";
        start.Environment["BASH_MAX_TIMEOUT_MS"] = "1800000";
        start.Environment["MCP_TOOL_TIMEOUT"] = "1800000";
        // Pinned commits predate advisories that would fail their restore; the same for both arms.
        start.Environment["NuGetAudit"] = "false";
        // The developer's usage log (Farol__UsageLog) records their real use: eval runs stay out of it.
        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("Farol__UsageLog", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(name);
        }

        var clock = Stopwatch.StartNew();
        var transcript = new Transcript(plugin is not null);
        using var process = new Process { StartInfo = start };
        process.Start();
        process.StandardInput.Close();
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromMinutes(task.Definition.TimeoutMinutes));
        var exit = "completed";
        await using (var output = new StreamWriter(transcriptPath))
        {
            try
            {
                while (await process.StandardOutput.ReadLineAsync(limit.Token) is { } line)
                {
                    await output.WriteLineAsync(line);
                    transcript.Read(line);
                }

                await process.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                exit = cancellationToken.IsCancellationRequested ? "cancelled" : "timeout";
            }
        }

        await File.WriteAllTextAsync(stderrPath, await stderr, CancellationToken.None);
        if (exit == "completed" && transcript.Subtype is null)
        {
            exit = "error";
        }

        return new AgentOutcome(
            exit,
            transcript.Subtype,
            transcript.Turns,
            transcript.CostUsd,
            transcript.Tokens,
            transcript.ToolCalls,
            transcript.Answer,
            transcript.IsolationProblem,
            transcript.RateLimited,
            clock.Elapsed);
    }

    /// <summary>
    /// Settings that leave out the user's own instructions, the CLAUDE.md and rules in Claude Code's config folder, which
    /// <c>--setting-sources project,local</c> still loads. The repository's CLAUDE.md files stay: they are part of the task.
    /// </summary>
    private static string UserInstructionsExcluded()
    {
        var config = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        var folder = Path.GetFullPath(config).Replace('\\', '/').TrimEnd('/');
        return new JsonObject { ["claudeMdExcludes"] = new JsonArray(folder + "/CLAUDE.md", folder + "/rules/**") }.ToJsonString();
    }

    /// <summary>The stream-json transcript, read line by line.</summary>
    private sealed class Transcript(bool farol)
    {
        private readonly Dictionary<string, TokenCount> _messageUsage = new(StringComparer.Ordinal);
        private TokenCount? _total;
        private string _lastText = string.Empty;
        private string? _result;

        public Dictionary<string, int> ToolCalls { get; } = new(StringComparer.Ordinal);

        public string? Subtype { get; private set; }

        public int Turns { get; private set; }

        public double CostUsd { get; private set; }

        public string? IsolationProblem { get; private set; }

        public bool RateLimited { get; private set; }

        public string Answer => _result ?? _lastText;

        // The result's per-model usage when the session finished; otherwise the usage of each model call seen.
        public TokenCount Tokens => _total ?? _messageUsage.Values.Aggregate(TokenCount.Zero, (sum, usage) => sum.Add(usage));

        public void Read(string line)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                return;
            }

            using (document)
            {
                var root = document.RootElement;
                switch (Text(root, "type"))
                {
                    case "system" when Text(root, "subtype") == "init":
                        CheckIsolation(root);
                        break;
                    case "assistant" when root.TryGetProperty("message", out var message):
                        ReadAssistant(message);
                        break;
                    // "allowed_warning" only says the window is nearly used up; the session goes on. A refusal stops it.
                    case "rate_limit_event" when root.TryGetProperty("rate_limit_info", out var info) && Text(info, "status") is { } status
                        && !status.StartsWith("allowed", StringComparison.Ordinal):
                        RateLimited = true;
                        break;
                    case "result":
                        ReadResult(root);
                        break;
                }
            }
        }

        private void CheckIsolation(JsonElement init)
        {
            // A repository's own servers are disabled in both arms (Repositories): a disabled server reaches neither.
            var servers = init.TryGetProperty("mcp_servers", out var list)
                ? list.EnumerateArray().Where(s => Text(s, "status") != "disabled").Select(s => $"{Text(s, "name")}={Text(s, "status")}").ToList()
                : [];
            var plugins = init.TryGetProperty("plugins", out var installed)
                ? installed.EnumerateArray().Where(p => Text(p, "source")?.EndsWith("@builtin", StringComparison.Ordinal) != true).Select(p => Text(p, "name") ?? "?").ToList()
                : [];
            List<string> expectedServers = farol ? ["plugin:farol:farol=connected"] : [];
            List<string> expectedPlugins = farol ? ["farol"] : [];
            if (!servers.SequenceEqual(expectedServers) || !plugins.SequenceEqual(expectedPlugins))
            {
                IsolationProblem = $"MCP servers [{string.Join(", ", servers)}], plugins [{string.Join(", ", plugins)}]";
            }
        }

        private void ReadAssistant(JsonElement message)
        {
            if (message.TryGetProperty("content", out var content))
            {
                foreach (var block in content.EnumerateArray())
                {
                    switch (Text(block, "type"))
                    {
                        case "tool_use" when Text(block, "name") is { } name:
                            ToolCalls[name] = ToolCalls.GetValueOrDefault(name) + 1;
                            break;
                        case "text" when Text(block, "text") is { Length: > 0 } text:
                            _lastText = text;
                            break;
                    }
                }
            }

            if (Text(message, "id") is { } id && message.TryGetProperty("usage", out var usage))
            {
                _messageUsage[id] = Usage(usage, "input_tokens", "output_tokens", "cache_read_input_tokens", "cache_creation_input_tokens");
            }
        }

        private void ReadResult(JsonElement result)
        {
            Subtype = Text(result, "subtype");
            Turns = result.TryGetProperty("num_turns", out var turns) && turns.ValueKind == JsonValueKind.Number ? turns.GetInt32() : 0;
            CostUsd = result.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number ? cost.GetDouble() : 0;
            _result = Text(result, "result");
            if (result.TryGetProperty("modelUsage", out var models) && models.ValueKind == JsonValueKind.Object)
            {
                _total = models.EnumerateObject()
                    .Select(m => Usage(m.Value, "inputTokens", "outputTokens", "cacheReadInputTokens", "cacheCreationInputTokens"))
                    .Aggregate(TokenCount.Zero, (sum, usage) => sum.Add(usage));
            }
        }

        private static TokenCount Usage(JsonElement usage, string input, string output, string cacheRead, string cacheCreation) =>
            new(Number(usage, input), Number(usage, output), Number(usage, cacheRead), Number(usage, cacheCreation));

        private static long Number(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : 0;

        private static string? Text(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
