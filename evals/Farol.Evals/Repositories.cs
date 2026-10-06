using System.Text.Json;
using System.Text.Json.Serialization;

namespace Farol.Evals;

/// <summary>
/// Where a task runs (evals/repos.json): a fixture of this repository, copied, or an open-source repository pinned to a
/// commit; "corpus" borrows a benchmark corpus's definition. <see cref="Prepare"/> runs in every fresh workspace.
/// </summary>
internal sealed record RepositoryDefinition(
    string? Fixture,
    string? Corpus,
    string? Repository,
    string? Commit,
    string? Workspace,
    IReadOnlyList<string>? Prepare);

/// <summary>A repository resolved to what a run needs.</summary>
internal sealed record EvalRepository(string Name, string? FixtureDirectory, string? Repository, string? Commit, string Workspace, IReadOnlyList<string> Prepare)
{
    public bool IsFixture => FixtureDirectory is not null;
}

/// <summary>A workspace a single run owns: the task's repository at its pinned state, prepared, committed as the baseline.</summary>
internal sealed class RunWorkspace(string directory, EvalRepository repository, string? cacheDirectory)
{
    public string Directory => directory;

    public EvalRepository Repository => repository;

    public string WorkspacePath => Path.Combine(directory, repository.Workspace);

    /// <summary>Removes the workspace: a worktree through git, a fixture copy directly. Only directories this harness made.</summary>
    public async Task DisposeAsync(CancellationToken cancellationToken)
    {
        if (cacheDirectory is not null)
        {
            await Git.RunAsync(cacheDirectory, cancellationToken, "worktree", "remove", "--force", directory);
        }

        if (System.IO.Directory.Exists(directory) && File.Exists(Path.Combine(Path.GetDirectoryName(directory)!, Repositories.Marker)))
        {
            DeleteTree(directory);
        }
    }

    // git keeps its objects read-only; a build server may still hold a file for a moment. Leftovers are only disk space.
    private static void DeleteTree(string directory)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                foreach (var file in System.IO.Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                System.IO.Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(1000 * attempt);
            }
        }
    }
}

internal sealed class Repositories : IDisposable
{
    public const string Marker = ".farol-evals";

    private readonly string _root;
    private readonly string _home;
    private readonly Dictionary<string, EvalRepository> _repositories;
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private readonly Lock _markerGate = new();

    public Repositories(string root, string home)
    {
        _root = root;
        _home = home;
        var json = File.ReadAllText(Path.Combine(root, "evals", "repos.json"));
        var definitions = JsonSerializer.Deserialize(json, EvalJson.Default.DictionaryStringRepositoryDefinition) ?? [];
        _repositories = definitions.ToDictionary(kv => kv.Key, kv => Resolve(kv.Key, kv.Value), StringComparer.OrdinalIgnoreCase);
    }

    public void Dispose() => _cacheGate.Dispose();

    public EvalRepository Get(string name) =>
        _repositories.TryGetValue(name, out var repository) ? repository : throw new InvalidOperationException($"No repository '{name}' in evals/repos.json.");

    /// <summary>A fresh, prepared workspace for one run, under a short path (deep legacy trees approach MAX_PATH).</summary>
    public async Task<RunWorkspace> CreateAsync(string repositoryName, CancellationToken cancellationToken)
    {
        var repository = Get(repositoryName);
        var parent = Path.Combine(_home, "w");
        System.IO.Directory.CreateDirectory(parent);
        lock (_markerGate)
        {
            if (!File.Exists(Path.Combine(parent, Marker)))
            {
                File.WriteAllText(Path.Combine(parent, Marker), "Workspaces of Farol eval runs; safe to delete.");
            }
        }

        var directory = Path.Combine(parent, Guid.NewGuid().ToString("N")[..8]);

        RunWorkspace workspace;
        if (repository.IsFixture)
        {
            Copy(repository.FixtureDirectory!, directory);
            await File.WriteAllTextAsync(Path.Combine(directory, ".gitignore"), "bin/\nobj/\npackages/\n.vs/\nTestResults/\n*.user\n", cancellationToken);
            await Git.RunAsync(directory, cancellationToken, "init", "-q");
            await Git.RunAsync(directory, cancellationToken, "config", "core.autocrlf", "false");
            await Git.RunAsync(directory, cancellationToken, "add", "-A");
            await Git.RunAsync(directory, cancellationToken, "-c", "user.name=Farol evals", "-c", "user.email=evals@farol.invalid", "commit", "-q", "-m", "baseline");
            workspace = new RunWorkspace(directory, repository, cacheDirectory: null);
        }
        else
        {
            var cache = await EnsureCacheAsync(repository, cancellationToken);
            await Git.RunAsync(cache, cancellationToken, "worktree", "add", "--detach", "-f", directory, repository.Commit!);
            workspace = new RunWorkspace(directory, repository, cache);
        }

        foreach (var step in repository.Prepare)
        {
            var result = await Shell.RunAsync(step, directory, TimeSpan.FromMinutes(20), cancellationToken);
            if (result.ExitCode != 0)
            {
                await workspace.DisposeAsync(CancellationToken.None);
                throw new InvalidOperationException($"Preparing {repositoryName} failed at '{step}':\n{Tail(result.Output)}");
            }
        }

        await DisableRepositoryMcpServersAsync(directory, cancellationToken);
        return workspace;
    }

    /// <summary>
    /// MCP servers a repository declares in its .mcp.json (Umbraco's: its own CMS and a browser) need services of their own
    /// and are not part of the experiment: both arms get them disabled in .claude/settings.local.json, which git is told to
    /// ignore, so no check sees it as a change.
    /// </summary>
    private static async Task DisableRepositoryMcpServersAsync(string directory, CancellationToken cancellationToken)
    {
        var declared = Path.Combine(directory, ".mcp.json");
        if (!File.Exists(declared))
        {
            return;
        }

        using var document = JsonDocument.Parse(
            await File.ReadAllTextAsync(declared, cancellationToken),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var names = document.RootElement.TryGetProperty("mcpServers", out var servers) ? servers.EnumerateObject().Select(s => s.Name).ToList() : [];
        var settings = Path.Combine(directory, ".claude", "settings.local.json");
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        await File.WriteAllTextAsync(settings, $"{{ \"disabledMcpjsonServers\": [{string.Join(", ", names.Select(n => $"\"{n}\""))}] }}\n", cancellationToken);

        var exclude = (await Git.TryRunAsync(directory, cancellationToken, "rev-parse", "--git-path", "info/exclude")).Output.Trim();
        exclude = Path.IsPathRooted(exclude) ? exclude : Path.Combine(directory, exclude);
        const string Entry = "/.claude/settings.local.json";
        if (!File.Exists(exclude) || !(await File.ReadAllLinesAsync(exclude, cancellationToken)).Contains(Entry))
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(exclude)!);
            await File.AppendAllTextAsync(exclude, Entry + "\n", cancellationToken);
        }
    }

    private async Task<string> EnsureCacheAsync(EvalRepository repository, CancellationToken cancellationToken)
    {
        var cache = Path.Combine(_home, "repos", repository.Name);
        await _cacheGate.WaitAsync(cancellationToken);
        try
        {
            if (!System.IO.Directory.Exists(Path.Combine(cache, ".git")))
            {
                System.IO.Directory.CreateDirectory(cache);
                await Git.RunAsync(cache, cancellationToken, "init", "-q");
                await Git.RunAsync(cache, cancellationToken, "config", "core.longpaths", "true");
                await Git.RunAsync(cache, cancellationToken, "remote", "add", "origin", repository.Repository!);
            }

            if ((await Git.TryRunAsync(cache, cancellationToken, "cat-file", "-e", repository.Commit + "^{commit}")).ExitCode != 0)
            {
                await Git.RunAsync(cache, cancellationToken, "fetch", "-q", "--depth", "1", "origin", repository.Commit!);
            }

            await Git.RunAsync(cache, cancellationToken, "worktree", "prune");
            return cache;
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private EvalRepository Resolve(string name, RepositoryDefinition definition)
    {
        if (definition.Corpus is { } corpus)
        {
            var lines = File.ReadAllLines(Path.Combine(_root, "benchmarks", "corpora", corpus + ".json")).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal));
            var borrowed = JsonSerializer.Deserialize(string.Join('\n', lines), EvalJson.Default.RepositoryDefinition)!;
            definition = borrowed with { Prepare = definition.Prepare ?? borrowed.Prepare };
        }

        var fixture = definition.Fixture is { } relative ? Path.GetFullPath(Path.Combine(_root, relative)) : null;
        return new EvalRepository(name, fixture, definition.Repository, definition.Commit, definition.Workspace ?? throw new InvalidOperationException($"{name}: no workspace."), definition.Prepare ?? []);
    }

    private static void Copy(string source, string destination)
    {
        foreach (var directory in System.IO.Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            if (!IsBuildOutput(relative))
            {
                System.IO.Directory.CreateDirectory(Path.Combine(destination, relative));
            }
        }

        foreach (var file in System.IO.Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (!IsBuildOutput(relative))
            {
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(destination, relative))!);
                File.Copy(file, Path.Combine(destination, relative));
            }
        }
    }

    private static bool IsBuildOutput(string relative) =>
        relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj" or "packages" or ".vs" or "TestResults");

    private static string Tail(string output) => output.Length <= 3000 ? output : output[^3000..];
}

internal static class Git
{
    public static async Task RunAsync(string directory, CancellationToken cancellationToken, params string[] arguments)
    {
        var result = await TryRunAsync(directory, cancellationToken, arguments);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed in {directory}:\n{result.Output}");
        }
    }

    public static Task<CommandResult> TryRunAsync(string directory, CancellationToken cancellationToken, params string[] arguments) =>
        Shell.RunAsync("git " + string.Join(' ', arguments.Select(a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a)), directory, TimeSpan.FromMinutes(10), cancellationToken);

    /// <summary>Files changed since the baseline (the commit the workspace started from), untracked ones included, with '/'.</summary>
    public static async Task<IReadOnlyList<string>> ChangedFilesAsync(string directory, CancellationToken cancellationToken)
    {
        var result = await TryRunAsync(directory, cancellationToken, "status", "--porcelain", "--untracked-files=all", "-z");
        return [.. result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Length > 3 ? entry[3..] : entry)
            .Select(path => path.Trim().Trim('"').Replace('\\', '/'))
            .Where(path => path.Length > 0)];
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(Dictionary<string, RepositoryDefinition>))]
[JsonSerializable(typeof(RepositoryDefinition))]
[JsonSerializable(typeof(TaskDefinition))]
internal sealed partial class EvalJson : JsonSerializerContext;
