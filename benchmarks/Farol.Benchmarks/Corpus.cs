using System.Text.Json;
using System.Text.Json.Serialization;

namespace Farol.Benchmarks;

/// <summary>
/// A pinned open-source solution and the calls to time on it (benchmarks/corpora/*.json). The queries name real
/// symbols, files and edits of that commit, so the numbers are comparable from run to run. <see cref="Prepare"/> lists
/// the commands benchmarks/prepare.ps1 runs in the checkout first (restore, generators).
/// </summary>
internal sealed record Corpus(
    string Name,
    string Repository,
    string Commit,
    string Workspace,
    IReadOnlyList<string> Prepare,
    IReadOnlyList<ToolCall> SymbolQueries,
    IReadOnlyList<string> References,
    IReadOnlyList<SourceEdit> Edits)
{
    public static Corpus Load(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize(stream, CorpusJson.Default.Corpus)
            ?? throw new InvalidDataException($"{path} is empty.");
    }
}

/// <summary>A tool call timed as a warm symbol query.</summary>
internal sealed record ToolCall(string Tool, Dictionary<string, JsonElement> Arguments)
{
    public string Label => $"{Tool} {string.Join(' ', Arguments.Values.Select(v => v.ToString()))}";
}

/// <summary>
/// An edit to one file, timed through dotnet_check: <see cref="Find"/> must occur exactly once. A "body" edit leaves the
/// file's declarations alone; a "declaration" edit changes them, so the projects that depend on the file are checked too.
/// </summary>
internal sealed record SourceEdit(string Path, string Find, string Replace, string Kind);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(Corpus))]
internal sealed partial class CorpusJson : JsonSerializerContext;
