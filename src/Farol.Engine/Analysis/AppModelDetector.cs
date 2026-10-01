using Farol.Engine.Workspaces;

namespace Farol.Engine.Analysis;

/// <summary>
/// Recognizes application models from what a project references and the content files it carries.
/// Referenced assembly names work the same for classic and SDK-style projects, which is the point.
/// </summary>
internal static class AppModelDetector
{
    private const int MaxFilesScanned = 20_000;

    private static readonly HashSet<string> MarkupExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".aspx", ".ascx", ".master", ".asmx", ".svc", ".razor", ".cshtml", ".vbhtml",
    };

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "packages", ".vs", ".git",
    };

    public static IReadOnlyList<string> DetectAppModels(IReadOnlySet<string> references, string projectDirectory, ProjectFileFacts facts)
    {
        var markup = ScanMarkup(projectDirectory);
        var models = new List<string>();

        if (references.Any(r => r.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase))
            || string.Equals(facts.Sdk, "Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase))
        {
            models.Add(markup.Contains(".razor") ? "ASP.NET Core (Blazor)" : "ASP.NET Core");
        }

        if (references.Contains("System.Web.Mvc"))
        {
            models.Add("ASP.NET MVC 5");
        }

        if (references.Contains("System.Web.Http"))
        {
            models.Add("ASP.NET Web API 2");
        }

        if (markup.Contains(".aspx") || markup.Contains(".ascx") || markup.Contains(".master"))
        {
            models.Add("WebForms");
        }

        if (markup.Contains(".asmx"))
        {
            models.Add("ASMX");
        }

        if (markup.Contains(".svc"))
        {
            models.Add("WCF service");
        }
        else if (references.Contains("System.ServiceModel"))
        {
            models.Add("WCF");
        }

        if (references.Contains("PresentationFramework"))
        {
            models.Add("WPF");
        }

        if (references.Contains("System.Windows.Forms"))
        {
            models.Add("WinForms");
        }

        if (references.Contains("EntityFramework"))
        {
            models.Add("EF6");
        }

        if (references.Any(r => r.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase)))
        {
            models.Add("EF Core");
        }

        return models;
    }

    private static HashSet<string> ScanMarkup(string directory)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scanned = 0;
        var pending = new Stack<string>([directory]);
        while (pending.Count > 0 && scanned < MaxFilesScanned)
        {
            var current = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(current))
            {
                scanned++;
                var extension = Path.GetExtension(file);
                if (MarkupExtensions.Contains(extension))
                {
                    found.Add(extension);
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

        return found;
    }
}
