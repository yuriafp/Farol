using Farol.Core;
using ModelContextProtocol;

namespace Farol.Tools.Infrastructure;

/// <summary>
/// Turns expected engine failures into MCP tool errors whose text says what to do next. The SDK
/// hides the message of any exception that is not an <see cref="McpException"/>.
/// </summary>
internal static class ToolGuard
{
    public static async Task<string> RunAsync(Func<Task<string>> action)
    {
        try
        {
            return await action();
        }
        catch (FarolException ex)
        {
            throw new McpException(ex.Hint is null ? ex.Message : $"{ex.Message} {ex.Hint}", ex);
        }
    }
}
