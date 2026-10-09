using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Farol.Core.Usage;

/// <summary>
/// One line of the usage log: what happened, when and how long it took. It carries Farol's own names (tools, error
/// codes, exception types) and counts, never source text, argument values, paths or solution names.
/// </summary>
public sealed record UsageEvent
{
    public const int SchemaVersion = 1;

    [JsonPropertyName("v")]
    public int Version { get; init; } = SchemaVersion;

    [JsonPropertyName("ts")]
    public DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("event")]
    public required string Kind { get; init; }

    /// <summary>One Farol process; the log fills it in.</summary>
    public string? Session { get; init; }

    public string? Farol { get; init; }

    public string? Os { get; init; }

    public bool? ReadOnly { get; init; }

    public bool? Offline { get; init; }

    public string? Client { get; init; }

    public string? ClientVersion { get; init; }

    /// <summary>The workspace as <see cref="HashName"/> gives it.</summary>
    public string? Workspace { get; init; }

    public string? Tool { get; init; }

    public string? Outcome { get; init; }

    /// <summary>Farol's error code (<see cref="ErrorCodes"/>) when <see cref="Outcome"/> is an error.</summary>
    public string? Error { get; init; }

    /// <summary>The exception type of an unexpected error or a crash.</summary>
    public string? Exception { get; init; }

    public long? Ms { get; init; }

    /// <summary>Whether the token budget cut the response.</summary>
    public bool? Cut { get; init; }

    /// <summary>Characters in the response.</summary>
    public int? Chars { get; init; }

    public int? Projects { get; init; }

    public int? LoadErrors { get; init; }

    public int? LoadWarnings { get; init; }

    /// <summary>
    /// An 8-character hash of a solution or project file's name, so the log tells workspaces apart without naming
    /// them; the same file name gives the same hash on every machine.
    /// </summary>
    public static string HashName(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path)).ToUpperInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..8];
    }

    internal static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>The kinds of <see cref="UsageEvent"/>.</summary>
public static class UsageEventKind
{
    /// <summary>A Farol process started with the usage log on.</summary>
    public const string Start = "start";

    /// <summary>The client of the process, written with its first tool call.</summary>
    public const string Session = "session";

    public const string Load = "load";

    /// <summary>The background warm-up after a load: compilations and search indexes.</summary>
    public const string Warm = "warm";

    public const string Call = "call";

    /// <summary>An exception nothing handled, right before the process ends.</summary>
    public const string Crash = "crash";
}

/// <summary>How a call, load or warm-up ended.</summary>
public static class UsageOutcome
{
    public const string Ok = "ok";
    public const string Error = "error";
    public const string Cancelled = "cancelled";
}
