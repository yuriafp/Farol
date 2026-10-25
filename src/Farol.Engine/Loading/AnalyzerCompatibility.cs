using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using Farol.Engine.Workspaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Farol.Engine.Loading;

/// <summary>
/// An analyzer or source generator built for a newer compiler than the one Farol runs does not run inside Farol, and the
/// code it generates is missing: a preview .NET SDK's Razor generator, for one, left hundreds of false errors in every
/// Blazor solution (AC-46). An SDK's own analyzers are taken from an installed SDK whose compiler Farol runs, unless a
/// global.json pins the SDK; anything else is a load issue that says what is missing and how to fix it.
/// </summary>
internal static class AnalyzerCompatibility
{
    private const string CompilerAssembly = "Microsoft.CodeAnalysis";

    private static readonly Version Compiler = typeof(Compilation).Assembly.GetName().Version!;

    private static readonly JsonDocumentOptions GlobalJsonOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static (Solution Solution, List<LoadIssue> Issues) WithRunnableAnalyzers(Solution solution, string workspaceDirectory)
    {
        var folders = solution.Projects
            .SelectMany(p => p.AnalyzerReferences.OfType<AnalyzerFileReference>())
            .Select(r => r.FullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .GroupBy(p => Path.GetDirectoryName(p)!, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Folder: g.Key, Files: g.Select(p => (Path: p, Needs: CompilerVersion(p))).ToList()))
            .Where(g => g.Files.Any(f => f.Needs > Compiler))
            .ToList();
        if (folders.Count == 0)
        {
            return (solution, []);
        }

        var pin = PinningGlobalJson(workspaceDirectory);
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var issues = new List<LoadIssue>();
        foreach (var (folder, files) in folders)
        {
            var newer = files.Where(f => f.Needs > Compiler).ToList();
            var needs = newer.Max(f => f.Needs)!;
            var sdk = SdkFolder.Of(folder);
            var other = sdk?.NewestOtherWith([.. files.Select(f => f.Path)]);
            if (sdk is not null && other is not null && pin is null)
            {
                foreach (var file in files)
                {
                    replacements[file.Path] = other.PathOf(sdk.Relative(file.Path));
                }

                var names = string.Join(", ", newer.Select(f => Path.GetFileName(f.Path)));
                issues.Add(new LoadIssue(
                    "warning",
                    $"{names} of .NET SDK {sdk.Version} {(newer.Count == 1 ? "needs" : "need")} {CompilerAssembly} {needs}, newer than the {Compiler} Farol runs: " +
                    $"Farol takes {(newer.Count == 1 ? "it" : "them")} from .NET SDK {other.Version} instead, in {Projects(solution, files.Select(f => f.Path))}. " +
                    $"Generated code may differ from what SDK {sdk.Version} generates; a global.json that pins SDK {other.Version} makes builds match."));
                continue;
            }

            var fix = sdk is null
                ? $"Use a version of it built for {CompilerAssembly} {Compiler} or earlier, or a Farol version that runs compiler {needs}."
                : pin is not null
                    ? $"{pin} pins .NET SDK {sdk.Version}; until a Farol version runs compiler {needs}, pin {(other is null ? "an earlier SDK" : $"SDK {other.Version}, which is installed,")} instead."
                    : $"Install an earlier .NET SDK and pin it in a global.json, or use a Farol version that runs compiler {needs}.";
            foreach (var (path, needed) in newer)
            {
                issues.Add(new LoadIssue(
                    "error",
                    $"Analyzer or source generator {path} needs {CompilerAssembly} {needed}, newer than the {Compiler} Farol runs, so the code it generates " +
                    $"is missing from {Projects(solution, [path])}: expect errors that a build does not have. {fix}"));
            }
        }

        return (replacements.Count == 0 ? solution : Replace(solution, replacements), issues);
    }

    /// <summary>The version of the compiler an analyzer assembly was built against; null when it references none, or is not one.</summary>
    internal static Version? CompilerVersion(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var image = new PEReader(stream);
            if (!image.HasMetadata)
            {
                return null;
            }

            var metadata = image.GetMetadataReader();
            foreach (var handle in metadata.AssemblyReferences)
            {
                var reference = metadata.GetAssemblyReference(handle);
                if (metadata.StringComparer.Equals(reference.Name, CompilerAssembly))
                {
                    return reference.Version;
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private static Solution Replace(Solution solution, Dictionary<string, string> replacements)
    {
        foreach (var project in solution.Projects.ToList())
        {
            if (!project.AnalyzerReferences.OfType<AnalyzerFileReference>().Any(r => replacements.ContainsKey(r.FullPath)))
            {
                continue;
            }

            solution = solution.WithProjectAnalyzerReferences(project.Id, project.AnalyzerReferences.Select(reference =>
            {
                if (reference is not AnalyzerFileReference file || !replacements.TryGetValue(file.FullPath, out var replacement))
                {
                    return reference;
                }

                file.AssemblyLoader.AddDependencyLocation(replacement);
                return new AnalyzerFileReference(replacement, file.AssemblyLoader);
            }));
        }

        return solution;
    }

    private static string Projects(Solution solution, IEnumerable<string> analyzers)
    {
        var paths = analyzers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = solution.Projects
            .Where(p => p.AnalyzerReferences.OfType<AnalyzerFileReference>().Any(r => paths.Contains(r.FullPath)))
            .Select(p => p.FilePath is null ? p.Name : Path.GetFileNameWithoutExtension(p.FilePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var shown = string.Join(", ", names.Take(5)) + (names.Count > 5 ? $" and {names.Count - 5} more" : string.Empty);
        return $"{names.Count} project(s): {shown}";
    }

    /// <summary>The global.json the .NET SDK reads for this folder, when it names an SDK version.</summary>
    private static string? PinningGlobalJson(string directory)
    {
        for (var folder = new DirectoryInfo(directory); folder is not null; folder = folder.Parent)
        {
            var path = Path.Combine(folder.FullName, "global.json");
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(path), GlobalJsonOptions);
                return json.RootElement.ValueKind == JsonValueKind.Object
                    && json.RootElement.TryGetProperty("sdk", out var sdk) && sdk.ValueKind == JsonValueKind.Object
                    && sdk.TryGetProperty("version", out _) ? path : null;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>A folder of an installed .NET SDK: dotnet/sdk/{version}/….</summary>
    private sealed record SdkFolder(string SdksDirectory, string Version)
    {
        private string Directory => Path.Combine(SdksDirectory, Version);

        public static SdkFolder? Of(string folder)
        {
            for (var directory = new DirectoryInfo(folder); directory?.Parent?.Parent is { } dotnet; directory = directory.Parent)
            {
                if (string.Equals(directory.Parent.Name, "sdk", StringComparison.OrdinalIgnoreCase)
                    && (File.Exists(Path.Combine(dotnet.FullName, "dotnet.exe")) || File.Exists(Path.Combine(dotnet.FullName, "dotnet"))))
                {
                    return new SdkFolder(directory.Parent.FullName, directory.Name);
                }
            }

            return null;
        }

        public string Relative(string path) => Path.GetRelativePath(Directory, path);

        public string PathOf(string relative) => Path.Combine(Directory, relative);

        /// <summary>The newest other installed SDK that has all of these files, each built for a compiler Farol runs.</summary>
        public SdkFolder? NewestOtherWith(IReadOnlyList<string> files) =>
            System.IO.Directory.EnumerateDirectories(SdksDirectory)
                .Select(d => new SdkFolder(SdksDirectory, Path.GetFileName(d)))
                .Where(s => !string.Equals(s.Version, Version, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.Version, SdkVersionComparer.Instance)
                .FirstOrDefault(s => files.All(f => File.Exists(s.PathOf(Relative(f))) && !(CompilerVersion(s.PathOf(Relative(f))) > Compiler)));
    }

    /// <summary>Orders SDK versions such as 10.0.401 and 11.0.100-rc.2.26504.105: a release after its previews.</summary>
    private sealed class SdkVersionComparer : IComparer<string>
    {
        public static readonly SdkVersionComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var (xCore, xPreview) = Split(x);
            var (yCore, yPreview) = Split(y);
            var byCore = xCore.CompareTo(yCore);
            if (byCore != 0 || xPreview == yPreview)
            {
                return byCore;
            }

            if (xPreview is null || yPreview is null)
            {
                return xPreview is null ? 1 : -1;
            }

            var xParts = xPreview.Split('.');
            var yParts = yPreview.Split('.');
            for (var i = 0; i < Math.Min(xParts.Length, yParts.Length); i++)
            {
                var byPart = int.TryParse(xParts[i], out var xNumber) && int.TryParse(yParts[i], out var yNumber)
                    ? xNumber.CompareTo(yNumber)
                    : string.CompareOrdinal(xParts[i], yParts[i]);
                if (byPart != 0)
                {
                    return byPart;
                }
            }

            return xParts.Length.CompareTo(yParts.Length);
        }

        private static (Version Core, string? Preview) Split(string? version)
        {
            var dash = version?.IndexOf('-', StringComparison.Ordinal) ?? -1;
            var core = dash < 0 ? version : version![..dash];
            return (System.Version.TryParse(core, out var parsed) ? parsed : new Version(0, 0), dash < 0 ? null : version![(dash + 1)..]);
        }
    }
}
