using System.Diagnostics;
using System.Runtime.InteropServices;
using Farol.Core;
using Farol.Core.Usage;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Farol.Host;

/// <summary>
/// The usage log on the host's side: opening it from the configuration, the start of the process, crashes, and one
/// event per tool call, measured around the SDK's handler.
/// </summary>
internal static class UsageRecorder
{
    public static string Directory(IConfiguration configuration) =>
        configuration[$"{HostConfiguration.Section}:UsageLogDirectory"] is { Length: > 0 } directory ? directory : UsageLogFile.DefaultDirectory;

    /// <summary>The log the configuration asks for. A directory that cannot be used turns the log off, never the server.</summary>
    public static IUsageLog Open(IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>($"{HostConfiguration.Section}:UsageLog"))
        {
            return NullUsageLog.Instance;
        }

        try
        {
            return new UsageLogFile(Directory(configuration));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // stderr: stdout carries the protocol.
            Console.Error.WriteLine($"Farol: the usage log stays off, its directory cannot be used: {ex.Message}");
            return NullUsageLog.Instance;
        }
    }

    public static void Start(IUsageLog usage, bool readOnly, bool offline)
    {
        usage.Write(new UsageEvent
        {
            Kind = UsageEventKind.Start,
            Farol = HostConfiguration.Version,
            Os = RuntimeInformation.OSDescription,
            ReadOnly = readOnly,
            Offline = offline,
        });
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            usage.Write(new UsageEvent { Kind = UsageEventKind.Crash, Exception = e.ExceptionObject.GetType().Name });
    }

    /// <summary>
    /// Measures every tool call; the first one also records the client the MCP handshake named. Only Farol's own tool
    /// names are kept: anything else a client sends is "other".
    /// </summary>
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> CallFilter(IUsageLog usage)
    {
        var sessionRecorded = 0;
        return next => async (context, cancellationToken) =>
        {
            if (Interlocked.Exchange(ref sessionRecorded, 1) == 0)
            {
                var client = context.Server.ClientInfo;
                usage.Write(new UsageEvent { Kind = UsageEventKind.Session, Client = client?.Name, ClientVersion = client?.Version });
            }

            var tool = context.Params?.Name is { } name && name.StartsWith("dotnet_", StringComparison.Ordinal) ? name : "other";
            var outcome = ToolCallOutcome.Begin();
            var clock = Stopwatch.StartNew();
            try
            {
                var result = await next(context, cancellationToken);
                var chars = result.Content.OfType<TextContentBlock>().Sum(t => t.Text.Length);
                usage.Write(Call(tool, clock, outcome, result.IsError == true ? UsageOutcome.Error : UsageOutcome.Ok, chars));
                return result;
            }
            catch (OperationCanceledException)
            {
                usage.Write(Call(tool, clock, outcome, UsageOutcome.Cancelled));
                throw;
            }
            catch (Exception ex)
            {
                // Farol's own errors reach here as the McpException the tool threw, with their code already recorded.
                if (outcome.ErrorCode is null)
                {
                    outcome.Fail(ErrorCodes.Unexpected, ex.GetType().Name);
                }

                usage.Write(Call(tool, clock, outcome, UsageOutcome.Error));
                throw;
            }
        };
    }

    private static UsageEvent Call(string tool, Stopwatch clock, ToolCallOutcome outcome, string result, int? chars = null) => new()
    {
        Kind = UsageEventKind.Call,
        Tool = tool,
        Outcome = result,
        Error = result == UsageOutcome.Error ? outcome.ErrorCode ?? ErrorCodes.Unexpected : null,
        Exception = result == UsageOutcome.Error ? outcome.ExceptionType : null,
        Ms = clock.ElapsedMilliseconds,
        Cut = outcome.Cut ? true : null,
        Chars = chars,
    };
}
