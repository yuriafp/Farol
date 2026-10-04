using System.Diagnostics;
using System.Globalization;

namespace Farol.Benchmarks;

internal sealed record Options(string CorpusPath, string Repository, string Host, string Output, int Rounds)
{
    public static Options? Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                return null;
            }

            values[args[i][2..]] = args[i + 1];
        }

        if (args.Length % 2 != 0 || !values.TryGetValue("corpus", out var corpus) || !values.TryGetValue("repo", out var repository))
        {
            return null;
        }

        var root = RepositoryRoot();
        var host = values.TryGetValue("host", out var h) ? h : Path.Combine(root, "src", "Farol.Host", "bin", Configuration, "net10.0", "Farol.Host.dll");
        var output = values.TryGetValue("out", out var o) ? o : Path.Combine(root, "artifacts", "benchmarks", Path.GetFileNameWithoutExtension(corpus));
        var rounds = values.TryGetValue("rounds", out var r) ? int.Parse(r, CultureInfo.InvariantCulture) : 5;
        return new Options(Path.GetFullPath(corpus), Path.GetFullPath(repository), Path.GetFullPath(host), Path.GetFullPath(output), rounds);
    }

    // bin/<configuration>/net10.0/ of this tool: time the host built in the same configuration.
    private static string Configuration =>
        new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Parent!.Name;

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Farol.slnx")))
            {
                return directory.FullName;
            }
        }

        return Directory.GetCurrentDirectory();
    }
}

internal static class Git
{
    public static string Head(string repository) => Run(repository, "rev-parse", "HEAD").Trim();

    public static IReadOnlyList<string> Files(string repository, params string[] patterns) =>
        Run(repository, ["ls-files", "-z", "--", .. patterns]).Split('\0', StringSplitOptions.RemoveEmptyEntries);

    private static string Run(string repository, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, UseShellExecute = false, WorkingDirectory = repository };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("git did not start.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output : throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed in {repository}.");
    }
}

/// <summary>Lines of C# and VB the corpus tracks in git, generated and test code included.</summary>
internal sealed record CorpusSize(long Lines, int Files)
{
    public static CorpusSize Measure(string repository)
    {
        var files = Git.Files(repository, "*.cs", "*.vb");
        long lines = 0;
        foreach (var file in files)
        {
            lines += File.ReadLines(Path.Combine(repository, file)).LongCount();
        }

        return new CorpusSize(lines, files.Count);
    }
}
