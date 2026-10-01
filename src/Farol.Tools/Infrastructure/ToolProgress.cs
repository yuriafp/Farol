using ModelContextProtocol;

namespace Farol.Tools.Infrastructure;

/// <summary>
/// Turns engine progress messages into MCP progress notifications. The SDK passes a no-op reporter when the client
/// sent no progress token, so long tools can always report.
/// </summary>
internal sealed class ToolProgress(IProgress<ProgressNotificationValue>? notifications) : IProgress<string>
{
    private int _step;

    public void Report(string value) =>
        notifications?.Report(new ProgressNotificationValue { Progress = Interlocked.Increment(ref _step), Message = value });
}
