namespace Farol.Engine.Legacy;

/// <summary>
/// A .NET Framework technology found in legacy code, how to recognize its APIs (namespace, type or member prefixes),
/// and what replaces it on modern .NET.
/// </summary>
public sealed record LegacyTechnology(string Name, string Area, IReadOnlyList<string> Prefixes, string Replacement)
{
    /// <summary>WinForms and WPF keep working on .NET, on the -windows target frameworks.</summary>
    public bool RunsOnWindowsDesktop { get; init; }

    /// <summary>The whole technology needs a rewrite or a new host, not API-by-API fixes.</summary>
    public bool IsRewrite { get; init; }
}

/// <summary>
/// What Farol knows about legacy technologies. Portability findings come from the target framework's real API surface;
/// this catalog only names the technology an API belongs to and the replacement to suggest.
/// </summary>
public static class LegacyCatalog
{
    public static IReadOnlyList<LegacyTechnology> Technologies { get; } =
    [
        new("WebForms", "Web", ["System.Web.UI"],
            "Razor Pages or Blazor: pages and controls are rewritten, there is no automated conversion. Migrate incrementally behind YARP with Microsoft.AspNetCore.SystemWebAdapters, page by page.")
        {
            IsRewrite = true,
        },
        new("ASP.NET MVC 5", "Web", ["System.Web.Mvc"],
            "ASP.NET Core MVC: controllers, filters and Razor views port with moderate changes (dotnet upgrade-assistant helps).")
        {
            IsRewrite = true,
        },
        new("ASP.NET Web API 2", "Web", ["System.Web.Http"],
            "ASP.NET Core controllers or minimal APIs.")
        {
            IsRewrite = true,
        },
        new("ASMX web services", "Web", ["System.Web.Services"],
            "ASP.NET Core Web API or gRPC; CoreWCF (BasicHttpBinding) when existing SOAP clients must keep working.")
        {
            IsRewrite = true,
        },
        new("JavaScriptSerializer", "Web", ["System.Web.Script.Serialization"],
            "System.Text.Json."),
        new("ASP.NET (System.Web)", "Web", ["System.Web"],
            "ASP.NET Core: middleware replaces HTTP modules and handlers, Program.cs replaces Global.asax, IHttpContextAccessor replaces HttpContext.Current. Microsoft.AspNetCore.SystemWebAdapters bridges an incremental migration.")
        {
            IsRewrite = true,
        },
        new("WCF", "Services", ["System.ServiceModel"],
            "CoreWCF hosts the same service contracts on ASP.NET Core (BasicHttp, NetTcp, WSHttp bindings); gRPC for new APIs. WCF clients keep working through the System.ServiceModel.* packages (dotnet-svcutil).")
        {
            IsRewrite = true,
        },
        new(".NET Remoting", "Services", ["System.Runtime.Remoting"],
            "gRPC, StreamJsonRpc or named pipes (System.IO.Pipes).")
        {
            IsRewrite = true,
        },
        new("MSMQ", "Services", ["System.Messaging"],
            "No .NET equivalent: Azure Service Bus, RabbitMQ or another broker.")
        {
            IsRewrite = true,
        },
        new("COM+ (Enterprise Services)", "Services", ["System.EnterpriseServices"],
            "No .NET equivalent: TransactionScope for transactions, a separate service for the rest.")
        {
            IsRewrite = true,
        },
        new("Windows Workflow", "Services", ["System.Activities", "System.Workflow"],
            "CoreWF or Elsa Workflows.")
        {
            IsRewrite = true,
        },
        new("BinaryFormatter", "Serialization",
            ["System.Runtime.Serialization.Formatters.Binary.BinaryFormatter", "System.Runtime.Serialization.Formatters.Soap", "System.Runtime.Serialization.NetDataContractSerializer"],
            "System.Text.Json, XmlSerializer or DataContractSerializer (MessagePack or protobuf-net for compact binary). BinaryFormatter throws PlatformNotSupportedException on .NET 9 and later; read data already stored with it through System.Formats.Nrbf while converting it."),
        new("System.Configuration", "Configuration", ["System.Configuration"],
            "Microsoft.Extensions.Configuration (appsettings.json, environment variables) with the options pattern. Stopgap: the System.Configuration.ConfigurationManager package reads app.config and web.config on .NET."),
        new("SqlClient (System.Data.SqlClient)", "Data", ["System.Data.SqlClient"],
            "Microsoft.Data.SqlClient: the same API in a new namespace."),
        new("OracleClient", "Data", ["System.Data.OracleClient"],
            "Oracle.ManagedDataAccess.Core."),
        new("LINQ to SQL", "Data", ["System.Data.Linq"],
            "EF Core."),
        new("Entity Framework 6", "Data", ["System.Data.Entity"],
            "EF 6.5 runs on .NET, so it can move first; EF Core is the long-term path (a different API)."),
        new("WinForms", "Desktop", ["System.Windows.Forms"],
            "Runs on net10.0-windows (UseWindowsForms); replace the controls .NET dropped (DataGrid → DataGridView, MainMenu → MenuStrip, ToolBar → ToolStrip).")
        {
            RunsOnWindowsDesktop = true,
        },
        new("WPF", "Desktop", ["System.Windows", "System.Xaml"],
            "Runs on net10.0-windows (UseWPF).")
        {
            RunsOnWindowsDesktop = true,
        },
        new("System.Drawing", "Desktop", ["System.Drawing"],
            "The System.Drawing.Common package, Windows only on .NET 7 and later; ImageSharp or SkiaSharp across platforms.")
        {
            RunsOnWindowsDesktop = true,
        },
        new("Code Access Security", "Runtime", ["System.Security.Permissions", "System.Security.Policy", "System.Security.SecurityManager"],
            "Not supported on .NET: rely on operating system permissions and process isolation."),
        new("AppDomains", "Runtime", ["System.AppDomain.CreateDomain", "System.AppDomain.Unload", "System.AppDomain.ExecuteAssembly", "System.AppDomainSetup"],
            "AssemblyLoadContext to load and unload code, or separate processes for isolation."),
        new("Thread.Abort", "Runtime", ["System.Threading.Thread.Abort", "System.Threading.Thread.Suspend", "System.Threading.Thread.Resume"],
            "Cooperative cancellation with CancellationToken."),
        new("WebRequest and WebClient", "Runtime", ["System.Net.WebClient", "System.Net.WebRequest", "System.Net.HttpWebRequest"],
            "HttpClient, created through IHttpClientFactory."),
        new("Event Log", "Runtime", ["System.Diagnostics.EventLog"],
            "The System.Diagnostics.EventLog package (Windows only), or ILogger with an event log provider."),
        new("Directory Services", "Runtime", ["System.DirectoryServices"],
            "The System.DirectoryServices package (Windows only) or System.DirectoryServices.Protocols."),
        new("System.Runtime.Caching", "Runtime", ["System.Runtime.Caching"],
            "Microsoft.Extensions.Caching.Memory (IMemoryCache); the System.Runtime.Caching package also runs on .NET."),
    ];

    /// <summary>
    /// The technology of an API by its full name ("System.Web.UI.Page", "System.AppDomain.CreateDomain"): the longest
    /// matching prefix wins, so System.Windows.Forms is WinForms and System.Windows is WPF.
    /// </summary>
    public static LegacyTechnology? Match(string fullName)
    {
        ArgumentNullException.ThrowIfNull(fullName);
        LegacyTechnology? best = null;
        var bestLength = -1;
        foreach (var technology in Technologies)
        {
            foreach (var prefix in technology.Prefixes)
            {
                if (prefix.Length > bestLength && Matches(fullName, prefix))
                {
                    best = technology;
                    bestLength = prefix.Length;
                }
            }
        }

        return best;
    }

    private static readonly string[] Areas = ["Project", "Web", "Services", "Desktop", "Data", "Serialization", "Configuration", "Runtime"];

    public static LegacyTechnology Named(string name) => Technologies.Single(t => t.Name == name);

    /// <summary>The order areas are listed in: web and services first, where the biggest rewrites are.</summary>
    public static int AreaOrder(string area) => Array.IndexOf(Areas, area) is var index and >= 0 ? index : Areas.Length;

    private static bool Matches(string fullName, string prefix) =>
        fullName.StartsWith(prefix, StringComparison.Ordinal)
        && (fullName.Length == prefix.Length || fullName[prefix.Length] is '.' or '+' or '`' or '(');
}
