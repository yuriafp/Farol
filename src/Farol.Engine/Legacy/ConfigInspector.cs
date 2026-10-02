using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Farol.Core;

namespace Farol.Engine.Legacy;

public sealed record ConfigSetting(string Key, string Value, bool Masked);

public sealed record ConfigConnectionString(string Name, string? Provider, string Value, bool Masked);

public sealed record ConfigBindingRedirect(string Assembly, string OldVersion, string NewVersion);

/// <summary>The WCF configuration: services with their endpoints, client endpoints, bindings, behaviors and hosting flags.</summary>
public sealed record ConfigServiceModel(
    IReadOnlyList<string> Services,
    IReadOnlyList<string> Clients,
    IReadOnlyList<string> Bindings,
    IReadOnlyList<string> Behaviors,
    IReadOnlyList<string> Hosting);

/// <summary>What a web.config or app.config holds, secrets masked, and how it maps onto appsettings.json.</summary>
public sealed record ConfigReport(
    string FilePath,
    IReadOnlyList<ConfigSetting> AppSettings,
    IReadOnlyList<ConfigConnectionString> ConnectionStrings,
    ConfigServiceModel? ServiceModel,
    IReadOnlyList<ConfigBindingRedirect> BindingRedirects,
    IReadOnlyList<string> SystemWeb,
    IReadOnlyList<string> OtherSections,
    IReadOnlyList<string> Transforms,
    string AppSettingsJson,
    IReadOnlyList<string> Mapping)
{
    public int MaskedCount => AppSettings.Count(s => s.Masked) + ConnectionStrings.Count(c => c.Masked);
}

/// <summary>
/// Reads .NET Framework configuration files. Secrets never leave this class: passwords and keys inside connection
/// strings, secret-looking app settings and machine keys are masked before anything is returned.
/// </summary>
public static partial class ConfigInspector
{
    public const string Mask = "***";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "configSections", "appSettings", "connectionStrings", "system.serviceModel", "runtime", "system.web", "startup",
    };

    /// <summary>web.config and app.config files directly in a project directory.</summary>
    public static IReadOnlyList<string> FindIn(string projectDirectory)
    {
        if (!Directory.Exists(projectDirectory))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(projectDirectory, "*.config")
            .Where(f => Path.GetFileName(f).Equals("web.config", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).Equals("app.config", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    public static ConfigReport Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        XElement root;
        try
        {
            root = XDocument.Load(path).Root ?? throw new XmlException("The file has no root element.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            throw new FarolException(ErrorCodes.InvalidArgument, $"'{Path.GetFileName(path)}' could not be read as XML: {ex.Message}", "Pass a web.config or app.config file.");
        }

        if (root.Name.LocalName != "configuration")
        {
            throw new FarolException(ErrorCodes.InvalidArgument, $"'{Path.GetFileName(path)}' is not a .NET configuration file (its root is <{root.Name.LocalName}>).", "Pass a web.config or app.config file.");
        }

        var appSettings = Child(root, "appSettings")?.Elements().Where(e => e.Name.LocalName == "add" && e.Attribute("key") is not null)
            .Select(e => Setting((string)e.Attribute("key")!, (string?)e.Attribute("value") ?? string.Empty))
            .ToList() ?? [];
        var connectionStrings = Child(root, "connectionStrings")?.Elements().Where(e => e.Name.LocalName == "add" && e.Attribute("name") is not null)
            .Select(e => ConnectionString((string)e.Attribute("name")!, (string?)e.Attribute("providerName"), (string?)e.Attribute("connectionString") ?? string.Empty))
            .ToList() ?? [];
        var serviceModel = Child(root, "system.serviceModel") is { } wcf ? ServiceModel(wcf) : null;
        var redirects = Child(root, "runtime")?.Descendants().Where(e => e.Name.LocalName == "dependentAssembly")
            .Select(Redirect)
            .OfType<ConfigBindingRedirect>()
            .ToList() ?? [];
        var systemWeb = Child(root, "system.web") is { } web ? SystemWeb(web) : [];
        var declared = Child(root, "configSections")?.Descendants().Where(e => e.Name.LocalName == "section").Select(e => (string?)e.Attribute("name")).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? [];
        var others = root.Elements().Select(e => e.Name.LocalName).Where(n => !Known.Contains(n)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n => declared.Contains(n) ? $"{n} (custom section)" : n)
            .ToList();
        var transforms = Transforms(path);

        return new ConfigReport(
            path,
            appSettings,
            connectionStrings,
            serviceModel,
            redirects,
            systemWeb,
            others,
            transforms,
            AppSettingsJson(appSettings, connectionStrings),
            Mapping(appSettings, connectionStrings, serviceModel, redirects, systemWeb, others, transforms));
    }

    /// <summary>True for setting names that usually hold credentials: passwords, secrets, tokens, keys.</summary>
    public static bool IsSecretName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return SecretName().IsMatch(name) || name.EndsWith("key", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Masks the values of password, key and token entries inside a connection string, keeping the rest readable.</summary>
    public static string MaskConnectionString(string value, out bool masked)
    {
        ArgumentNullException.ThrowIfNull(value);
        var any = false;
        var result = SecretPair().Replace(value, m =>
        {
            any = true;
            return $"{m.Groups["key"].Value}{m.Groups["separator"].Value}{Mask}";
        });
        masked = any;
        return result;
    }

    private static ConfigSetting Setting(string key, string value)
    {
        if (IsSecretName(key) && value.Length > 0)
        {
            return new ConfigSetting(key, Mask, Masked: true);
        }

        // A connection string kept in appSettings still hides its password.
        var masked = MaskConnectionString(value, out var any);
        return new ConfigSetting(key, masked, any);
    }

    private static ConfigConnectionString ConnectionString(string name, string? provider, string value)
    {
        var masked = MaskConnectionString(value, out var any);
        return new ConfigConnectionString(name, provider, masked, any);
    }

    private static ConfigServiceModel ServiceModel(XElement wcf)
    {
        var services = Child(wcf, "services")?.Elements().Where(e => e.Name.LocalName == "service").Select(service =>
        {
            var endpoints = service.Elements().Where(e => e.Name.LocalName == "endpoint").Select(Endpoint).ToList();
            return $"{(string?)service.Attribute("name") ?? "(unnamed)"}: {(endpoints.Count == 0 ? "default endpoints" : string.Join(", ", endpoints))}";
        }).ToList() ?? [];
        var clients = Child(wcf, "client")?.Elements().Where(e => e.Name.LocalName == "endpoint")
            .Select(e => $"{(string?)e.Attribute("name") ?? "(unnamed)"}: {Endpoint(e)}{((string?)e.Attribute("address") is { Length: > 0 } address ? $" → {address}" : string.Empty)}")
            .ToList() ?? [];
        var bindings = Child(wcf, "bindings")?.Elements()
            .SelectMany(kind => kind.Elements().Where(e => e.Name.LocalName == "binding").Select(b => $"{kind.Name.LocalName}/{(string?)b.Attribute("name") ?? "(default)"}"))
            .ToList() ?? [];
        var behaviors = Child(wcf, "behaviors")?.Elements()
            .SelectMany(kind => kind.Elements().Where(e => e.Name.LocalName == "behavior").Select(b =>
                $"{kind.Name.LocalName}/{((string?)b.Attribute("name") is { Length: > 0 } name ? name : "(default)")}: {string.Join(", ", b.Elements().Select(e => e.Name.LocalName))}"))
            .ToList() ?? [];
        var hosting = Child(wcf, "serviceHostingEnvironment") is { } environment
            ? environment.Attributes().Select(a => $"{a.Name.LocalName}={a.Value}").ToList()
            : [];
        return new ConfigServiceModel(services, clients, bindings, behaviors, hosting);
    }

    private static string Endpoint(XElement endpoint)
    {
        var address = (string?)endpoint.Attribute("address");
        var contract = (string?)endpoint.Attribute("contract");
        return $"{(string?)endpoint.Attribute("binding") ?? "(default binding)"}{(contract is null ? string.Empty : $" ({contract})")}{(string.IsNullOrEmpty(address) || endpoint.Parent?.Name.LocalName == "client" ? string.Empty : $" at '{address}'")}";
    }

    private static ConfigBindingRedirect? Redirect(XElement dependent)
    {
        var identity = dependent.Elements().FirstOrDefault(e => e.Name.LocalName == "assemblyIdentity");
        var redirect = dependent.Elements().FirstOrDefault(e => e.Name.LocalName == "bindingRedirect");
        return identity?.Attribute("name") is { } name && redirect is not null
            ? new ConfigBindingRedirect(name.Value, (string?)redirect.Attribute("oldVersion") ?? "?", (string?)redirect.Attribute("newVersion") ?? "?")
            : null;
    }

    private static List<string> SystemWeb(XElement web)
    {
        var entries = new List<string>();
        foreach (var element in web.Elements())
        {
            var name = element.Name.LocalName;
            var children = element.Elements().Count();
            var entry = name switch
            {
                "machineKey" => "machineKey (keys masked)",
                "identity" => $"identity impersonate={(string?)element.Attribute("impersonate") ?? "false"}{(element.Attribute("password") is null ? string.Empty : " (password masked)")}",
                "httpModules" or "httpHandlers" => $"{name}: {children} entr{(children == 1 ? "y" : "ies")}",
                _ => element.Attributes().Any()
                    ? $"{name} {string.Join(' ', element.Attributes().Where(a => !IsSecretName(a.Name.LocalName)).Select(a => $"{a.Name.LocalName}={a.Value}"))}".TrimEnd()
                    : children > 0 ? $"{name}: {children} entr{(children == 1 ? "y" : "ies")}" : name,
            };
            entries.Add(entry);
        }

        return entries;
    }

    private static List<string> Transforms(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        return [.. Directory.EnumerateFiles(directory, $"{stem}.*.config")
            .Select(Path.GetFileName)
            .OfType<string>()
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// appsettings.json for the same values: app settings at the top level (keys with ':' become nested sections, as
    /// IConfiguration reads them), connection strings under ConnectionStrings. Masked values stay masked.
    /// </summary>
    private static string AppSettingsJson(List<ConfigSetting> appSettings, List<ConfigConnectionString> connectionStrings)
    {
        var root = new JsonObject();
        foreach (var setting in appSettings)
        {
            var path = setting.Key.Split(':', StringSplitOptions.RemoveEmptyEntries);
            var parent = root;
            var placed = true;
            foreach (var section in path[..^1])
            {
                if (parent[section] is JsonObject existing)
                {
                    parent = existing;
                }
                else if (parent[section] is null)
                {
                    var created = new JsonObject();
                    parent[section] = created;
                    parent = created;
                }
                else
                {
                    placed = false;
                    break;
                }
            }

            if (placed && path.Length > 0 && parent[path[^1]] is null)
            {
                parent[path[^1]] = setting.Value;
            }
            else
            {
                root[setting.Key] = setting.Value;
            }
        }

        if (connectionStrings.Count > 0)
        {
            var section = new JsonObject();
            foreach (var connection in connectionStrings)
            {
                section[connection.Name] = connection.Value;
            }

            root["ConnectionStrings"] = section;
        }

        return root.ToJsonString(JsonOptions);
    }

    private static List<string> Mapping(
        List<ConfigSetting> appSettings,
        List<ConfigConnectionString> connectionStrings,
        ConfigServiceModel? serviceModel,
        List<ConfigBindingRedirect> redirects,
        List<string> systemWeb,
        List<string> others,
        List<string> transforms)
    {
        var mapping = new List<string>();
        if (appSettings.Count > 0)
        {
            mapping.Add("appSettings → top-level keys: ConfigurationManager.AppSettings[\"Key\"] becomes IConfiguration[\"Key\"] (a key with ':' is a section); bind related keys to an options class.");
        }

        if (connectionStrings.Count > 0)
        {
            mapping.Add("connectionStrings → ConnectionStrings: ConfigurationManager.ConnectionStrings[\"Name\"].ConnectionString becomes IConfiguration.GetConnectionString(\"Name\"); providerName has no equivalent (the ADO.NET package decides).");
        }

        var masked = appSettings.Count(s => s.Masked) + connectionStrings.Count(c => c.Masked);
        if (masked > 0)
        {
            mapping.Add($"{masked} masked value(s) hold secrets: keep them out of appsettings.json — user secrets in development, environment variables or a key vault in production.");
        }

        if (serviceModel is not null)
        {
            mapping.Add("system.serviceModel → CoreWCF configures services in code (CoreWCF.ConfigurationManager can read this section); WCF clients use generated proxies (dotnet-svcutil) configured in code.");
        }

        if (redirects.Count > 0)
        {
            mapping.Add($"runtime/assemblyBinding ({redirects.Count} binding redirect(s)) → not needed: .NET unifies assembly versions at run time; drop them.");
        }

        if (systemWeb.Count > 0)
        {
            mapping.Add("system.web → ASP.NET Core: compilation and httpRuntime have no equivalent; authentication, session state, custom errors, modules and handlers move to middleware in Program.cs.");
        }

        if (others.Contains("system.webServer", StringComparer.OrdinalIgnoreCase))
        {
            mapping.Add("system.webServer → stays in a web.config that IIS reads for the ASP.NET Core module (handlers, rewrite rules), or moves to middleware.");
        }

        if (others.Any(o => o.EndsWith("(custom section)", StringComparison.Ordinal)))
        {
            mapping.Add("custom sections → a JSON section each, bound to an options class.");
        }

        if (transforms.Count > 0)
        {
            mapping.Add($"transforms ({string.Join(", ", transforms)}) → appsettings.{{Environment}}.json files.");
        }

        return mapping;
    }

    private static XElement? Child(XElement element, string name) => element.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    [GeneratedRegex(@"password|passwd|pwd|secret|token|api[-_]?key|access[-_]?key|private[-_]?key|credential|signature|connection[-_]?string", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretName();

    [GeneratedRegex(@"(?<key>\b(?:password|pwd|user\s+password|account\s*key|shared\s*access\s*key|shared\s*access\s*signature|access\s*key|client\s*secret|api\s*key|token))(?<separator>\s*=\s*)(?<value>""[^""]*""|'[^']*'|[^;]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretPair();
}
