using Farol.Core;
using Farol.Core.Usage;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Farol.Tools.Infrastructure;

/// <summary>
/// Turns expected engine failures into MCP tool errors whose text says what to do next. The SDK
/// hides the message of any exception that is not an <see cref="McpException"/>. The usage log gets
/// the error's code, which the SDK's error result no longer carries.
/// </summary>
internal static class ToolGuard
{
    public static async Task<CallToolResult> RunAsync<T>(Func<Task<ToolOutput<T>>> action) =>
        (CallToolResult)await RunAsync(async () => (object)(await action()).ToResult());

    public static async Task<string> RunAsync(Func<Task<string>> action) => (string)await RunAsync(async () => (object)await action());

    private static async Task<object> RunAsync(Func<Task<object>> action)
    {
        try
        {
            return await action();
        }
        catch (FarolException ex)
        {
            ToolCallOutcome.Current?.Fail(ex.Code);
            throw new McpException(ex.Hint is null ? ex.Message : $"{ex.Message} {ex.Hint}", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ToolCallOutcome.Current?.Fail(ErrorCodes.Unexpected, ex.GetType().Name);
            throw;
        }
    }
}
