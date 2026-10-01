using Farol.Core;
using Farol.Engine;
using Farol.Host;
using Farol.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    // appsettings.json lives next to the executable, never in the repository being analyzed.
    ContentRootPath = AppContext.BaseDirectory,
});

// --workspace / --root / --autoload, on top of Farol__* environment variables and appsettings.json.
builder.Configuration.AddCommandLine(args, HostConfiguration.SwitchMappings);

// Under stdio, stdout is the MCP channel: every log line must go to stderr.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(CallerContext.LocalProcess);
builder.Services.AddFarolEngine(options => builder.Configuration.GetSection(HostConfiguration.Section).Bind(options));

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation { Name = "farol", Version = HostConfiguration.Version };
        options.ServerInstructions = ServerInstructions.Text;
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly(ToolsAssembly.Assembly);

await builder.Build().RunAsync();
