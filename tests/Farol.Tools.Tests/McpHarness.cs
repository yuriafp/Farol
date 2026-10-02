using System.IO.Pipelines;
using Farol.Core;
using Farol.Engine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Farol.Tools.Tests;

/// <summary>
/// The real Farol tool set behind an in-memory MCP transport: tests go through the protocol
/// (schemas, argument binding, error mapping) exactly as a client would.
/// </summary>
public sealed class McpHarness : IAsyncDisposable
{
    private readonly IHost _host;

    private McpHarness(IHost host, McpClient client)
    {
        _host = host;
        Client = client;
    }

    public McpClient Client { get; }

    public static async Task<McpHarness> StartAsync(string rootDirectory, CancellationToken cancellationToken, Action<FarolEngineOptions>? configure = null)
    {
        Pipe clientToServer = new(), serverToClient = new();

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(CallerContext.LocalProcess);
        builder.Services.AddFarolEngine(o =>
        {
            o.RootDirectory = rootDirectory;
            o.AutoLoad = false;
            configure?.Invoke(o);
        });
        builder.Services
            .AddMcpServer()
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
            .WithToolsFromAssembly(ToolsAssembly.Assembly)
            .WithPromptsFromAssembly(ToolsAssembly.Assembly);

        var host = builder.Build();
        await host.StartAsync(cancellationToken);

        var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()),
            cancellationToken: cancellationToken);

        return new McpHarness(host, client);
    }

    public async Task<(bool IsError, string Text)> CallAsync(string tool, Dictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var result = await Client.CallToolAsync(tool, arguments, cancellationToken: cancellationToken);
        var text = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(t => t.Text));
        return (result.IsError is true, text);
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await _host.StopAsync();
        _host.Dispose();
    }
}
