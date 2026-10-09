namespace Farol.Core.Usage;

/// <summary>
/// What one tool call reports about itself to the usage log: the code of the error it ended with and whether the token
/// budget cut its response. The host opens one per call, and code running inside the call fills it in.
/// </summary>
public sealed class ToolCallOutcome
{
    private static readonly AsyncLocal<ToolCallOutcome?> Scope = new();

    private ToolCallOutcome()
    {
    }

    /// <summary>The outcome of the tool call this code runs in; null outside one, or when nothing records calls.</summary>
    public static ToolCallOutcome? Current => Scope.Value;

    public string? ErrorCode { get; private set; }

    public string? ExceptionType { get; private set; }

    public bool Cut { get; private set; }

    /// <summary>Starts an outcome for the calling flow: everything it awaits from here on fills in the same one.</summary>
    public static ToolCallOutcome Begin()
    {
        var outcome = new ToolCallOutcome();
        Scope.Value = outcome;
        return outcome;
    }

    /// <summary>Records the error the call ended with; the first one recorded wins.</summary>
    public void Fail(string code, string? exceptionType = null)
    {
        ErrorCode ??= code;
        ExceptionType ??= exceptionType;
    }

    public void MarkCut() => Cut = true;
}
