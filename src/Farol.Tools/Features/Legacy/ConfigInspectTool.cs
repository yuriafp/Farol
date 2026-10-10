using System.ComponentModel;
using Farol.Core;
using Farol.Core.Paths;
using Farol.Core.Text;
using Farol.Engine.Legacy;
using Farol.Engine.Workspaces;
using Farol.Tools.Infrastructure;
using ModelContextProtocol.Server;

namespace Farol.Tools.Features.Legacy;

[McpServerToolType]
public sealed class ConfigInspectTool(WorkspaceManager workspaces)
{
    private const int MaxFilesScanned = 20_000;

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "packages", "node_modules", ".vs", ".git" };

    [McpServerTool(Name = "dotnet_config_inspect", Title = "Inspect a web.config or app.config", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Reads a web.config or app.config with secrets masked (passwords and keys inside connection strings, secret-looking app settings): app " +
        "settings, connection strings, WCF services and endpoints, system.web settings, binding redirects and transforms, plus how each part maps " +
        "onto ASP.NET Core configuration and the appsettings.json to start from. Without path, lists the workspace's configuration files.")]
    public Task<string> Run(
        [Description("The web.config or app.config, relative to the root as responses write paths (e.g. Legacy.Web/Web.config). Omit to list them.")] string? path = null,
        [McpHeader(ToolParameters.WorkspaceHeader), Description(ToolParameters.WorkspaceDescription)] string? workspace = null,
        [Description(ToolParameters.MaxTokensDescription)] int maxTokens = TokenBudget.DefaultTokens,
        CancellationToken cancellationToken = default) =>
        ToolGuard.RunAsync(() =>
        {
            var root = workspaces.RootDirectory;
            var text = new ResponseBuilder(maxTokens);
            if (string.IsNullOrWhiteSpace(path))
            {
                var session = workspaces.GetSession(workspace);
                List(text, session.Target.Directory, root, cancellationToken);
                return Task.FromResult(text.ToString());
            }

            var directory = workspaces.TargetDirectory(workspace);
            var file = workspaces.Paths.Resolve(path, directory);
            if (!File.Exists(file))
            {
                if (directory is null && !Path.IsPathRooted(path.Trim()))
                {
                    // The path may be written from a workspace's folder: when no workspace resolves, that is the error to report.
                    workspaces.GetSession(workspace);
                }

                throw new FarolException(ErrorCodes.InvalidArgument, $"'{path}' does not exist.", "Call without path to list the configuration files, then pass one as the list writes it.");
            }

            Render(text, ConfigInspector.Read(file), root);
            return Task.FromResult(text.ToString());
        });

    private static void List(ResponseBuilder text, string directory, string root, CancellationToken cancellationToken)
    {
        var files = Find(directory).ToList();
        if (files.Count == 0)
        {
            text.Line("no web.config or app.config in the workspace.");
            return;
        }

        text.Line($"configuration files ({files.Count}); call again with path for the full content and the appsettings.json mapping:");
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string summary;
            try
            {
                var config = ConfigInspector.Read(file);
                var parts = new List<string>
                {
                    $"{config.AppSettings.Count} app setting(s)",
                    $"{config.ConnectionStrings.Count} connection string(s)",
                };
                if (config.MaskedCount > 0)
                {
                    parts.Add($"{config.MaskedCount} secret(s)");
                }

                if (config.ServiceModel is { } wcf)
                {
                    parts.Add($"WCF: {wcf.Services.Count} service(s), {wcf.Clients.Count} client endpoint(s)");
                }

                if (config.BindingRedirects.Count > 0)
                {
                    parts.Add($"{config.BindingRedirects.Count} binding redirect(s)");
                }

                summary = string.Join(", ", parts);
            }
            catch (FarolException ex)
            {
                summary = ex.Message;
            }

            if (!text.TryLine($"- {DisplayPath.From(root, file)}: {summary}"))
            {
                text.More(files.Count - files.IndexOf(file), "raise maxTokens");
                return;
            }
        }
    }

    private static void Render(ResponseBuilder text, ConfigReport config, string root)
    {
        text.Line($"config: {DisplayPath.From(root, config.FilePath)}{(config.Transforms.Count > 0 ? $" · transforms: {string.Join(", ", config.Transforms)}" : string.Empty)}");
        if (config.AppSettings.Count > 0)
        {
            text.Line($"app settings ({config.AppSettings.Count}):");
            foreach (var setting in config.AppSettings)
            {
                text.TryLine($"- {setting.Key} = {setting.Value}{(setting.Masked ? " (masked)" : string.Empty)}");
            }
        }

        if (config.ConnectionStrings.Count > 0)
        {
            text.Line($"connection strings ({config.ConnectionStrings.Count}):");
            foreach (var connection in config.ConnectionStrings)
            {
                text.TryLine($"- {connection.Name}{(connection.Provider is null ? string.Empty : $" ({connection.Provider})")}: {connection.Value}{(connection.Masked ? " (secrets masked)" : string.Empty)}");
            }
        }

        if (config.SystemWeb.Count > 0)
        {
            text.TryLine($"system.web: {string.Join(" · ", config.SystemWeb)}");
        }

        if (config.ServiceModel is { } wcf)
        {
            text.Line("WCF (system.serviceModel):");
            foreach (var service in wcf.Services)
            {
                text.TryLine($"- service {service}");
            }

            foreach (var client in wcf.Clients)
            {
                text.TryLine($"- client {client}");
            }

            if (wcf.Bindings.Count > 0)
            {
                text.TryLine($"- bindings: {string.Join(", ", wcf.Bindings)}");
            }

            foreach (var behavior in wcf.Behaviors)
            {
                text.TryLine($"- behavior {behavior}");
            }

            if (wcf.Hosting.Count > 0)
            {
                text.TryLine($"- hosting: {string.Join(", ", wcf.Hosting)}");
            }
        }

        if (config.BindingRedirects.Count > 0)
        {
            text.Line($"binding redirects ({config.BindingRedirects.Count}):");
            foreach (var redirect in config.BindingRedirects)
            {
                text.TryLine($"- {redirect.Assembly} {redirect.OldVersion} → {redirect.NewVersion}");
            }
        }

        if (config.OtherSections.Count > 0)
        {
            text.TryLine($"other sections: {string.Join(", ", config.OtherSections)}");
        }

        text.Line("appsettings.json (secrets stay masked):");
        text.Line("```json");
        foreach (var line in config.AppSettingsJson.Split('\n'))
        {
            text.Line(line.TrimEnd('\r'));
        }

        text.Line("```");
        if (config.Mapping.Count > 0)
        {
            text.Line("mapping to ASP.NET Core:");
            foreach (var mapping in config.Mapping)
            {
                text.TryLine($"- {mapping}");
            }
        }
    }

    private static IEnumerable<string> Find(string directory)
    {
        var scanned = 0;
        var pending = new Stack<string>([directory]);
        var found = new List<string>();
        while (pending.Count > 0 && scanned < MaxFilesScanned)
        {
            var current = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(current, "*.config"))
            {
                scanned++;
                var name = Path.GetFileName(file);
                if (name.Equals("web.config", StringComparison.OrdinalIgnoreCase) || name.Equals("app.config", StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(file);
                }
            }

            foreach (var child in Directory.EnumerateDirectories(current))
            {
                if (!SkippedDirectories.Contains(Path.GetFileName(child)))
                {
                    pending.Push(child);
                }
            }
        }

        return found.Order(StringComparer.OrdinalIgnoreCase);
    }
}
