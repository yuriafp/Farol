using System.Diagnostics;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Farol.Benchmarks;

/// <summary>
/// The Farol host as its own process, driven over stdio exactly as an MCP client drives it. Starting the process here,
/// rather than through the SDK's stdio transport, keeps a handle on it for memory readings.
/// </summary>
internal sealed class FarolServer : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task _stderr;

    private FarolServer(Process process, Task stderr, McpClient client, TimeSpan toolsListed, int toolCount)
    {
        _process = process;
        _stderr = stderr;
        Client = client;
        ToolsListed = toolsListed;
        ToolCount = toolCount;
    }

    public McpClient Client { get; }

    /// <summary>Process start to the tools/list answer: what a client waits before it can show Farol's tools.</summary>
    public TimeSpan ToolsListed { get; }

    public int ToolCount { get; }

    /// <summary>The largest working set the server process has had so far, in bytes.</summary>
    public long PeakWorkingSet
    {
        get
        {
            _process.Refresh();
            return _process.PeakWorkingSet64;
        }
    }

    public static async Task<FarolServer> StartAsync(string hostAssembly, string root, string workspace, string logPath, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = root,
        };
        foreach (var argument in new[] { hostAssembly, "--root", root, "--workspace", workspace })
        {
            start.ArgumentList.Add(argument);
        }

        var clock = Stopwatch.StartNew();
        var process = Process.Start(start) ?? throw new InvalidOperationException("dotnet did not start.");
        var stderr = CopyToFileAsync(process.StandardError, logPath);
        var client = await McpClient.CreateAsync(
            new StreamClientTransport(process.StandardInput.BaseStream, process.StandardOutput.BaseStream),
            cancellationToken: cancellationToken);
        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
        return new FarolServer(process, stderr, client, clock.Elapsed, tools.Count);
    }

    public async Task<ToolAnswer> CallAsync(string tool, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var result = await Client.CallToolAsync(tool, arguments, cancellationToken: cancellationToken);
        clock.Stop();
        var text = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(t => t.Text));
        return new ToolAnswer(clock.Elapsed, text, result.IsError is true);
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            try
            {
                await _process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                _process.Kill(entireProcessTree: true);
            }
        }

        await _stderr;
        _process.Dispose();
    }

    private static async Task CopyToFileAsync(StreamReader reader, string path)
    {
        await using var log = new StreamWriter(path);
        while (await reader.ReadLineAsync() is { } line)
        {
            await log.WriteLineAsync(line);
        }
    }
}

internal sealed record ToolAnswer(TimeSpan Elapsed, string Text, bool IsError);
