using Farol.Core;
using Farol.Core.Usage;
using Farol.Engine;
using Farol.Host;
using Farol.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

// MCP clients never ask for help: a person or an agent checking the install does, so the server does not start.
if (Help.IsRequested(args))
{
    Console.Out.WriteLine(Help.Text);
    return;
}

// Nor does it for the report on the usage log this machine kept.
if (UsageReportCommand.IsRequested(args))
{
    Console.Out.WriteLine(UsageReportCommand.Run(args));
    return;
}

args = HostConfiguration.ExpandFlags(args);
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    // appsettings.json lives next to the executable, never in the repository being analyzed.
    ContentRootPath = AppContext.BaseDirectory,
});

// --workspace / --root / --autoload / --read-only / --offline / --usage-log, on top of Farol__* environment variables
// and appsettings.json.
builder.Configuration.AddCommandLine(args, HostConfiguration.SwitchMappings);

// Under stdio, stdout is the MCP channel: every log line must go to stderr.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

var readOnly = builder.Configuration.GetValue<bool>($"{HostConfiguration.Section}:ReadOnly");
var offline = builder.Configuration.GetValue<bool>($"{HostConfiguration.Section}:Offline");
var usage = UsageRecorder.Open(builder.Configuration);
builder.Services.AddSingleton(CallerContext.LocalProcess);
builder.Services.AddSingleton<IUsageLog>(usage);
builder.Services.AddFarolEngine(options => builder.Configuration.GetSection(HostConfiguration.Section).Bind(options));

var server = builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation { Name = "farol", Version = HostConfiguration.Version };
        options.ServerInstructions = ServerInstructions.For(readOnly, offline);
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly(ToolsAssembly.Assembly)
    .WithPromptsFromAssembly(ToolsAssembly.Assembly);

if (usage.Enabled)
{
    server.WithRequestFilters(filters => filters.AddCallToolFilter(UsageRecorder.CallFilter(usage)));
    UsageRecorder.Start(usage, readOnly, offline);
}

await builder.Build().RunAsync();
