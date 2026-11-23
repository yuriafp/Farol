using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Farol.Engine.Loading;

/// <summary>An assembly reference as a classic project writes it: the name, and the HintPath when it has one (full path).</summary>
internal sealed record ClassicReference(string Name, string? HintPath);

/// <summary>A package of a packages.config file, or of a classic project's PackageReference items.</summary>
internal sealed record ConfigPackage(string Id, string Version);

/// <summary>
/// What a heuristic load needs from a classic (.NET Framework, non-SDK) project file, read as XML without MSBuild: the
/// properties of one Debug configuration, the compiled files, the references and the analyzers. Conditions are
/// evaluated for that configuration; imports and targets do not run, so whatever they would add (generated code,
/// references added by a NuGet .targets file) is missing.
/// </summary>
internal sealed partial record ClassicProjectFile(
    string Path,
    string Language,
    string Name,
    string AssemblyName,
    string OutputType,
    string TargetFrameworkVersion,
    IReadOnlyDictionary<string, string> Properties,
    IReadOnlyList<(string Path, IReadOnlyList<string> Folders)> Compile,
    IReadOnlyList<ClassicReference> References,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> Analyzers,
    IReadOnlyList<string> Imports,
    IReadOnlyList<ConfigPackage> Packages,
    IReadOnlyList<ConfigPackage> PackageReferences,
    bool HasXaml)
{
    public string Directory => System.IO.Path.GetDirectoryName(Path)!;

    public string Configuration => Property("Configuration") ?? "Debug";

    public string Platform => Property("Platform") ?? "AnyCPU";

    public string? Property(string name) => Properties.TryGetValue(name, out var value) && value.Length > 0 ? value : null;

    public static ClassicProjectFile Read(string path, string? solutionDirectory)
    {
        var root = XDocument.Load(path).Root ?? throw new XmlException("The project file is empty.");
        var directory = System.IO.Path.GetDirectoryName(path)!;
        var (configuration, platform) = ChooseConfiguration(root);
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MSBuildProjectDirectory"] = directory,
            ["MSBuildThisFileDirectory"] = directory + System.IO.Path.DirectorySeparatorChar,
            ["MSBuildProjectFullPath"] = path,
            ["MSBuildProjectFile"] = System.IO.Path.GetFileName(path),
            ["ProjectDir"] = directory + System.IO.Path.DirectorySeparatorChar,
            ["MSBuildProjectName"] = System.IO.Path.GetFileNameWithoutExtension(path),
            ["SolutionDir"] = (solutionDirectory ?? directory) + System.IO.Path.DirectorySeparatorChar,
            ["Configuration"] = configuration,
            ["Platform"] = platform,
        };
        var evaluator = new ConditionEvaluator(properties, directory);

        // Pass 1, as MSBuild: every property, in order. Pass 2: the items, with the final properties.
        foreach (var group in Groups(root, "PropertyGroup", evaluator))
        {
            foreach (var property in group.Elements().Where(evaluator.Applies))
            {
                properties[property.Name.LocalName] = evaluator.Expand(property.Value.Trim());
            }
        }

        var compile = new List<(string Path, IReadOnlyList<string> Folders)>();
        var references = new List<ClassicReference>();
        var projectReferences = new List<string>();
        var analyzers = new List<string>();
        var imports = new List<string>();
        var packageReferences = new List<ConfigPackage>();
        var hasXaml = false;
        foreach (var item in Groups(root, "ItemGroup", evaluator).SelectMany(g => g.Elements()).Where(evaluator.Applies))
        {
            if (item.Name.LocalName == "Compile" && (string?)item.Attribute("Remove") is { } remove)
            {
                var removed = Patterns(directory, evaluator.Expand(remove));
                compile.RemoveAll(c => removed.Any(m => m(c.Path)));
                continue;
            }

            var include = evaluator.Expand(((string?)item.Attribute("Include") ?? string.Empty).Trim());
            if (include.Length == 0 || include.Contains("$(", StringComparison.Ordinal) || include.Contains("@(", StringComparison.Ordinal))
            {
                continue;
            }

            switch (item.Name.LocalName)
            {
                case "Compile":
                    var link = Metadata(item, "Link", evaluator) is { } l ? evaluator.Expand(l) : null;
                    var exclude = (string?)item.Attribute("Exclude") is { } e ? evaluator.Expand(e) : null;
                    foreach (var file in Files(directory, include, exclude))
                    {
                        compile.Add((file, Folders(directory, file, link)));
                    }

                    break;
                case "Reference":
                    references.Add(Reference(directory, include, Metadata(item, "HintPath", evaluator) is { } h ? evaluator.Expand(h) : null));
                    break;
                case "COMReference" or "COMFileReference":
                    // Interop assemblies are generated by a target (ResolveComReference): not available here.
                    break;
                case "ProjectReference":
                    projectReferences.Add(Existing(Full(directory, include)));
                    break;
                case "Analyzer":
                    analyzers.Add(Existing(Full(directory, include)));
                    break;
                case "Import":
                    imports.Add(include);
                    break;
                case "PackageReference":
                    if ((Metadata(item, "Version", evaluator) is { } v ? evaluator.Expand(v) : null) is { Length: > 0 } version)
                    {
                        packageReferences.Add(new ConfigPackage(include, version));
                    }

                    break;
                case "Page" or "ApplicationDefinition":
                    hasXaml = true;
                    break;
            }
        }

        var language = System.IO.Path.GetExtension(path).Equals(".vbproj", StringComparison.OrdinalIgnoreCase) ? "Visual Basic" : "C#";
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        return new ClassicProjectFile(
            path,
            language,
            name,
            properties.GetValueOrDefault("AssemblyName") is { Length: > 0 } assembly ? assembly : name,
            properties.GetValueOrDefault("OutputType") is { Length: > 0 } output ? output : "Library",
            properties.GetValueOrDefault("TargetFrameworkVersion") is { Length: > 0 } frameworkVersion ? frameworkVersion : "v4.0",
            properties,
            compile.DistinctBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToList(),
            // A reference listed twice (once per condition, say): the one with a HintPath says where it is.
            references.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.FirstOrDefault(r => r.HintPath is not null) ?? g.First()).ToList(),
            projectReferences.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            analyzers.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            imports.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            PackagesConfig(System.IO.Path.Combine(directory, "packages.config")),
            packageReferences.DistinctBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList(),
            hasXaml);
    }

    // "System.Web, Version=4.0.0.0, …" → its name; "..\lib\Foo.dll" (a file path in Include) → Foo, with that path.
    private static ClassicReference Reference(string directory, string include, string? hint)
    {
        if (include.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || include.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            hint ??= include;
            include = System.IO.Path.GetFileNameWithoutExtension(include.Replace('\\', '/').Split('/')[^1]);
        }

        return new ClassicReference(include.Split(',')[0].Trim(), hint is null || hint.Contains("$(", StringComparison.Ordinal) ? null : Existing(Full(directory, hint)));
    }

    /// <summary>
    /// The configuration a heuristic load stands for: Debug|AnyCPU when the project has it, else its first Debug
    /// configuration, else its first configuration (from the '$(Configuration)|$(Platform)' == '…' conditions).
    /// </summary>
    private static (string Configuration, string Platform) ChooseConfiguration(XElement root)
    {
        var pairs = new List<(string Configuration, string Platform)>();
        foreach (var condition in root.Descendants().Select(e => (string?)e.Attribute("Condition")).OfType<string>())
        {
            if (ConfigurationAndPlatform().Match(condition) is { Success: true } both)
            {
                pairs.Add((both.Groups[1].Value.Trim(), both.Groups[2].Value.Trim()));
            }
            else if (ConfigurationOnly().Match(condition) is { Success: true } only)
            {
                pairs.Add((only.Groups[1].Value.Trim(), string.Empty));
            }
        }

        pairs = [.. pairs.Where(p => p.Configuration.Length > 0)];
        bool IsDebug((string Configuration, string Platform) p) => p.Configuration.Equals("Debug", StringComparison.OrdinalIgnoreCase);
        bool IsAnyCpu((string Configuration, string Platform) p) => p.Platform.Replace(" ", string.Empty, StringComparison.Ordinal).Equals("AnyCPU", StringComparison.OrdinalIgnoreCase);
        var chosen = pairs.FirstOrDefault(p => IsDebug(p) && IsAnyCpu(p));
        if (chosen.Configuration is null)
        {
            chosen = pairs.FirstOrDefault(p => IsDebug(p) && p.Platform.Length > 0);
        }

        if (chosen.Configuration is null)
        {
            chosen = pairs.FirstOrDefault(IsDebug);
        }

        if (chosen.Configuration is null)
        {
            chosen = pairs.FirstOrDefault(p => p.Platform.Length > 0);
        }

        if (chosen.Configuration is null)
        {
            return ("Debug", "AnyCPU");
        }

        return (chosen.Configuration, chosen.Platform.Length > 0 ? chosen.Platform : "AnyCPU");
    }

    [GeneratedRegex(@"'\s*\$\(Configuration\)\s*\|\s*\$\(Platform\)\s*'\s*==\s*'([^'|]*)\|([^']*)'", RegexOptions.IgnoreCase)]
    private static partial Regex ConfigurationAndPlatform();

    [GeneratedRegex(@"'\s*\$\(Configuration\)\s*'\s*==\s*'([^']*)'", RegexOptions.IgnoreCase)]
    private static partial Regex ConfigurationOnly();

    // The groups of that name at the top of the project and inside the branch each <Choose> takes, in document order.
    private static IEnumerable<XElement> Groups(XElement parent, string localName, ConditionEvaluator evaluator)
    {
        foreach (var element in parent.Elements())
        {
            if (element.Name.LocalName == localName && evaluator.Applies(element))
            {
                yield return element;
            }
            else if (element.Name.LocalName == "Choose")
            {
                var branch = element.Elements().FirstOrDefault(w => w.Name.LocalName == "When" && evaluator.Applies(w))
                    ?? element.Elements().FirstOrDefault(w => w.Name.LocalName == "Otherwise");
                if (branch is not null)
                {
                    foreach (var group in Groups(branch, localName, evaluator))
                    {
                        yield return group;
                    }
                }
            }
        }
    }

    private static List<ConfigPackage> PackagesConfig(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return [.. XDocument.Load(path).Descendants().Where(e => e.Name.LocalName == "package")
                .Select(e => ((string?)e.Attribute("id"), (string?)e.Attribute("version")))
                .Where(p => p.Item1 is { Length: > 0 } && p.Item2 is { Length: > 0 })
                .Select(p => new ConfigPackage(p.Item1!, p.Item2!))];
        }
        catch (XmlException)
        {
            return [];
        }
    }

    // Item metadata as MSBuild reads it: child elements whose condition holds, the last one winning, else the attribute.
    private static string? Metadata(XElement item, string name, ConditionEvaluator evaluator) =>
        item.Elements().LastOrDefault(e => e.Name.LocalName == name && evaluator.Applies(e))?.Value.Trim() ?? (string?)item.Attribute(name);

    internal static string Full(string directory, string path) =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, path.Replace('\\', System.IO.Path.DirectorySeparatorChar)));

    // The file in the case it has on disk (Linux), or the path as written.
    private static string Existing(string path) => PathCase.File(path) ?? path;

    private static bool IsWildcard(string path) => path.Contains('*', StringComparison.Ordinal) || path.Contains('?', StringComparison.Ordinal);

    /// <summary>The files an Include names: literal paths as they are, wildcards expanded (a missing file is kept: the build would fail on it too).</summary>
    private static IEnumerable<string> Files(string directory, string include, string? exclude)
    {
        var excluded = Patterns(directory, exclude);
        foreach (var part in include.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!IsWildcard(part))
            {
                var full = Full(directory, part);
                var file = PathCase.File(full) ?? full;
                if (!excluded.Any(m => m(file)))
                {
                    yield return file;
                }

                continue;
            }

            var (baseDirectory, matches) = Glob(directory, part);
            if (!System.IO.Directory.Exists(baseDirectory))
            {
                continue;
            }

            foreach (var file in System.IO.Directory.EnumerateFiles(baseDirectory, "*", SearchOption.AllDirectories))
            {
                if (matches(file) && !excluded.Any(m => m(file)))
                {
                    yield return file;
                }
            }
        }
    }

    /// <summary>A test per pattern of an Exclude or Remove: a literal path, or a wildcard (<c>**\obj\**</c>, <c>Old\*.cs</c>).</summary>
    private static List<Func<string, bool>> Patterns(string directory, string? patterns)
    {
        var tests = new List<Func<string, bool>>();
        foreach (var part in (patterns ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IsWildcard(part))
            {
                tests.Add(Glob(directory, part).Matches);
            }
            else
            {
                var full = Full(directory, part);
                tests.Add(f => string.Equals(f, full, StringComparison.OrdinalIgnoreCase));
            }
        }

        return tests;
    }

    // "Shared\**\*.cs" → (dir/Shared, a test for files below it whose relative path matches "**/*.cs"), ignoring case as Windows does.
    private static (string Directory, Func<string, bool> Matches) Glob(string directory, string pattern)
    {
        // The fixed part keeps its root ("/repo/…", "C:\…", "\\server\…"): Full leaves a rooted path rooted.
        var normalized = pattern.Replace('\\', '/');
        var cut = normalized.LastIndexOf('/', normalized.IndexOfAny(['*', '?']));
        var fixedPart = cut < 0 ? "." : normalized[..(cut + 1)];
        var rest = normalized[(cut + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        var full = Full(directory, fixedPart);
        var baseDirectory = PathCase.Directory(full) ?? full;
        var regex = new System.Text.StringBuilder("^");
        for (var i = 0; i < rest.Count; i++)
        {
            var last = i == rest.Count - 1;
            if (rest[i] == "**")
            {
                regex.Append(last ? "(?:[^/]+/)*[^/]+" : "(?:[^/]+/)*");
                continue;
            }

            regex.Append(Regex.Escape(rest[i]).Replace(@"\*", "[^/]*", StringComparison.Ordinal).Replace(@"\?", "[^/]", StringComparison.Ordinal));
            if (!last)
            {
                regex.Append('/');
            }
        }

        var compiled = new Regex(regex.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        // A prefix test, not GetRelativePath: on Linux that is case-sensitive, and the pattern's case may not be the disk's.
        var prefix = System.IO.Path.TrimEndingDirectorySeparator(baseDirectory) + System.IO.Path.DirectorySeparatorChar;
        return (baseDirectory, file =>
            file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && compiled.IsMatch(file[prefix.Length..].Replace('\\', '/')));
    }

    // The folders a document shows under: its Link's, or its path's relative to the project.
    private static List<string> Folders(string directory, string file, string? link)
    {
        var relative = link ?? System.IO.Path.GetRelativePath(directory, file);
        if (relative.StartsWith("..", StringComparison.Ordinal))
        {
            return [];
        }

        var folders = relative.Replace('\\', '/').Split('/');
        return [.. folders.Take(folders.Length - 1)];
    }
}
