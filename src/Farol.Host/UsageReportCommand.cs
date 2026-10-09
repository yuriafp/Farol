using Farol.Core.Usage;
using Microsoft.Extensions.Configuration;

namespace Farol.Host;

/// <summary><c>farol --usage-report [--days N]</c>: sums the usage log up without starting the server.</summary>
internal static class UsageReportCommand
{
    private const string Switch = "--usage-report";

    public static bool IsRequested(IEnumerable<string> args) => args.Contains(Switch, StringComparer.OrdinalIgnoreCase);

    public static string Run(string[] args)
    {
        // The sources the server reads its settings from, so the report finds the log where the server wrote it.
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
            .AddEnvironmentVariables()
            .AddCommandLine(HostConfiguration.ExpandFlags([.. args.Where(a => !a.Equals(Switch, StringComparison.OrdinalIgnoreCase))]))
            .Build();
        return UsageReport.Build(UsageRecorder.Directory(configuration), configuration.GetValue("days", UsageReport.DefaultDays), DateTimeOffset.Now);
    }
}
