using System.Diagnostics;
using Farol.Benchmarks;

// Times Farol on a pinned open-source solution against the performance targets of spec 001 (AC-35).
// Usage: Farol.Benchmarks --corpus benchmarks/corpora/<name>.json --repo <checkout of its commit, restored>
//        [--host <Farol.Host.dll>] [--out <directory>] [--rounds <n>]
// Exit code: 0 every target met, 1 a target missed or a call failed, 2 bad arguments or setup.

var options = Options.Parse(args);
if (options is null)
{
    await Console.Error.WriteLineAsync("Usage: Farol.Benchmarks --corpus <corpus.json> --repo <checkout> [--host <Farol.Host.dll>] [--out <dir>] [--rounds <n>]");
    return 2;
}

var corpus = Corpus.Load(options.CorpusPath);
var head = Git.Head(options.Repository);
if (!head.StartsWith(corpus.Commit, StringComparison.OrdinalIgnoreCase))
{
    await Console.Error.WriteLineAsync($"{options.Repository} is at {head}, but {Path.GetFileName(options.CorpusPath)} names {corpus.Commit}: the queries only fit that commit.");
    return 2;
}

if (!File.Exists(options.Host))
{
    await Console.Error.WriteLineAsync($"No Farol host at {options.Host}: build src/Farol.Host first, or pass --host.");
    return 2;
}

var size = CorpusSize.Measure(options.Repository);
Directory.CreateDirectory(options.Output);
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

Console.WriteLine($"{corpus.Name}: {size.Lines:N0} lines in {size.Files:N0} files; server log in {options.Output}");
var clock = Stopwatch.StartNew();
await using var server = await FarolServer.StartAsync(options.Host, options.Repository, corpus.Workspace, Path.Combine(options.Output, "server.log"), cancel.Token);
var run = new BenchmarkRun(corpus, options.Repository, server, options.Rounds, Console.Out);
await run.RunAsync(clock, cancel.Token);

var version = server.Client.ServerInfo?.Version ?? "?";
var markdown = Report.Markdown(corpus, size, version, server, run);
await File.WriteAllTextAsync(Path.Combine(options.Output, "report.md"), markdown);
await File.WriteAllTextAsync(Path.Combine(options.Output, "results.json"), Report.Json(corpus, size, version, server, run));
await File.WriteAllTextAsync(Path.Combine(options.Output, "load-status.txt"), run.LoadStatus);
Console.WriteLine();
Console.WriteLine(markdown);

// In GitHub Actions, the report also becomes the job summary, and the results annotations anyone can read.
if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary)
{
    await File.AppendAllTextAsync(summary, markdown + Environment.NewLine);
}

if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
{
    foreach (var annotation in Report.Annotations(corpus, server, run))
    {
        Console.WriteLine(annotation);
    }
}

return run.Passed ? 0 : 1;
