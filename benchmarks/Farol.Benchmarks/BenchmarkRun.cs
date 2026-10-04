using System.Diagnostics;
using System.Text;

namespace Farol.Benchmarks;

/// <summary>
/// One benchmark of one corpus against a running server: load, warm symbol queries, find_references, and dotnet_check
/// after single-file edits, with the spec 001 targets (Non-Functional Requirements, AC-35).
/// </summary>
internal sealed class BenchmarkRun(Corpus corpus, string repository, FarolServer server, int rounds, TextWriter progress)
{
    public Series SymbolQueries { get; } = new("Warm symbol queries", 100);

    public Series FindReferences { get; } = new("dotnet_find_references", 1000);

    public Series Check { get; } = new("dotnet_check after editing one file", 2000);

    public TimeSpan Load { get; private set; }

    public string LoadStatus { get; private set; } = "";

    public long PeakWorkingSet { get; private set; }

    public List<string> Failures { get; } = [];

    public const long MemoryTarget = 3L * 1024 * 1024 * 1024;

    public bool Passed =>
        Failures.Count == 0 && SymbolQueries.Passed && FindReferences.Passed && Check.Passed && PeakWorkingSet < MemoryTarget;

    public async Task RunAsync(Stopwatch sinceStart, CancellationToken cancellationToken)
    {
        progress.WriteLine($"Loading {corpus.Workspace}...");
        var load = await server.CallAsync("dotnet_workspace", Arguments(("action", "load")), cancellationToken);
        Load = sinceStart.Elapsed;
        LoadStatus = load.Text;
        if (load.IsError)
        {
            Failures.Add($"dotnet_workspace load failed: {load.Text}");
            return;
        }

        progress.WriteLine($"Loaded in {Load.TotalSeconds:N0} s; warming up...");

        // Warm-up, not timed: every query once, so symbol indexes exist, and one reference search, so the compilations do.
        foreach (var query in corpus.SymbolQueries)
        {
            await CallAsync(query.Tool, query.Arguments.ToDictionary(a => a.Key, a => (object?)a.Value), query.Label, series: null, cancellationToken);
        }

        await CallAsync("dotnet_find_references", Arguments(("symbol", corpus.References[0])), $"warm-up {corpus.References[0]}", series: null, cancellationToken);

        progress.WriteLine($"Symbol queries: {corpus.SymbolQueries.Count} x {rounds}...");
        for (var round = 0; round < rounds; round++)
        {
            foreach (var query in corpus.SymbolQueries)
            {
                await CallAsync(query.Tool, query.Arguments.ToDictionary(a => a.Key, a => (object?)a.Value), query.Label, SymbolQueries, cancellationToken);
            }
        }

        // Each symbol once: a repeated search reuses the semantic models the first one built, and agents rarely repeat one.
        progress.WriteLine($"References: {corpus.References.Count - 1}...");
        foreach (var symbol in corpus.References.Skip(1))
        {
            await CallAsync("dotnet_find_references", Arguments(("symbol", symbol)), symbol, FindReferences, cancellationToken);
        }

        progress.WriteLine($"Edits: {corpus.Edits.Count}...");
        foreach (var edit in corpus.Edits)
        {
            await TimeEditAsync(edit, cancellationToken);
        }

        PeakWorkingSet = server.PeakWorkingSet;
    }

    /// <summary>Applies the edit, times dotnet_check the way the plugin's hook calls it, then puts the file back.</summary>
    private async Task TimeEditAsync(SourceEdit edit, CancellationToken cancellationToken)
    {
        var path = Path.Combine(repository, edit.Path);
        var original = await File.ReadAllBytesAsync(path, cancellationToken);
        var encoding = DetectEncoding(original);
        var text = encoding.GetString(original.AsSpan(encoding.Preamble.Length));
        var at = text.IndexOf(edit.Find, StringComparison.Ordinal);
        if (at < 0 || text.IndexOf(edit.Find, at + 1, StringComparison.Ordinal) >= 0)
        {
            Failures.Add($"Edit of {edit.Path}: the text to find must occur exactly once.");
            return;
        }

        var edited = string.Concat(text.AsSpan(0, at), edit.Replace, text.AsSpan(at + edit.Find.Length));
        try
        {
            await File.WriteAllBytesAsync(path, [.. encoding.Preamble, .. encoding.GetBytes(edited)], cancellationToken);
            await CallAsync("dotnet_check", Arguments(("edited", edit.Path)), $"{edit.Kind} edit of {edit.Path}", Check, cancellationToken);
        }
        finally
        {
            await File.WriteAllBytesAsync(path, original, CancellationToken.None);
        }

        // Back to the loaded state, untimed, so each edit is measured alone.
        await CallAsync("dotnet_check", Arguments(("edited", edit.Path)), $"revert {edit.Path}", series: null, cancellationToken);
    }

    private async Task CallAsync(string tool, Dictionary<string, object?> arguments, string label, Series? series, CancellationToken cancellationToken)
    {
        var answer = await server.CallAsync(tool, arguments, cancellationToken);
        if (answer.IsError)
        {
            Failures.Add($"{label}: {FirstLine(answer.Text)}");
            return;
        }

        // An ambiguous name answers with candidates instead of searching: fast, but not the call meant to be timed.
        if (answer.Text.Contains("Call again with the id", StringComparison.Ordinal))
        {
            Failures.Add($"{label}: the name is ambiguous; name the symbol by its id in the corpus file.");
            return;
        }

        series?.Add(label, answer.Elapsed);
    }

    private static Dictionary<string, object?> Arguments(params (string Name, string Value)[] arguments) =>
        arguments.ToDictionary(a => a.Name, a => (object?)a.Value);

    private static UTF8Encoding DetectEncoding(byte[] bytes) =>
        bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: true) : new UTF8Encoding(false);

    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();
}
