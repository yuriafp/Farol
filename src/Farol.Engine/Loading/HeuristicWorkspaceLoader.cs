using System.Collections.Concurrent;
using System.Diagnostics;
using System.Xml;
using System.Xml.Linq;
using Farol.Engine.Packages;
using Farol.Engine.Workspaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.Extensions.Logging;
using NuGet.Frameworks;
using NuGet.Versioning;

namespace Farol.Engine.Loading;

/// <summary>
/// Loads a solution without an MSBuild that can evaluate .NET Framework projects (Linux, or Windows without Visual
/// Studio or Build Tools). Classic projects are read as XML (<see cref="ClassicProjectFile"/>): their files, their
/// references resolved against the .NET Framework reference assemblies and packages.config packages, their analyzers,
/// and compiler options built the way csc and vbc read them. SDK-style projects still load through MSBuildWorkspace,
/// which runs them in the .NET SDK's build host. Imports and targets of classic projects do not run, so code they
/// generate (XAML code-behind parts, for one) is missing; the load says so.
/// </summary>
public sealed partial class HeuristicWorkspaceLoader(MSBuildWorkspaceLoader msbuild, PackageFeeds feeds, ILogger<HeuristicWorkspaceLoader> logger)
{
    public const string Name = "heuristic";

    public async Task<LoadedWorkspace> LoadAsync(
        WorkspaceTarget target,
        IReadOnlyDictionary<string, string> properties,
        Action<ProjectLoadProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var stopwatch = Stopwatch.StartNew();
        var issues = new List<LoadIssue>();
        var listed = (await SolutionProjects.ListAsync(target, cancellationToken)).Select(p => PathCase.File(p) ?? p).ToList();
        foreach (var absent in listed.Where(p => !File.Exists(p)))
        {
            issues.Add(new LoadIssue("error", $"{absent}: the project file does not exist."));
        }

        var existing = listed.Where(File.Exists).ToList();
        var classicPaths = existing.Where(p => !ProjectFileInspector.Inspect(p).IsSdkStyle).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sdkPaths = existing.Where(p => !classicPaths.Contains(p)).ToList();

        // Classic projects first (XML only): those listed, those SDK-style projects reference, and theirs in turn. An
        // SDK-style project a classic one references loads through MSBuild with the others.
        // Until neither set grows: classic → SDK-style → classic chains of unlisted projects.
        List<ClassicProjectFile> classic;
        HashSet<string> classicSet;
        var walked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            classicPaths.UnionWith(SdkClosure([.. sdkPaths.Where(walked.Add)]).Where(p => !ProjectFileInspector.Inspect(p).IsSdkStyle));
            classic = await ReadClassicAsync(classicPaths, target, issues, cancellationToken);
            classicSet = classic.Select(c => c.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var added = classic.SelectMany(c => c.ProjectReferences)
                .Where(r => File.Exists(r) && !classicSet.Contains(r) && !sdkPaths.Contains(r, StringComparer.OrdinalIgnoreCase) && ProjectFileInspector.Inspect(r).IsSdkStyle)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (added.Count == 0)
            {
                break;
            }

            sdkPaths.AddRange(added);
        }
        var frameworks = new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>(StringComparer.OrdinalIgnoreCase);

        Workspace? workspace = null;
        try
        {
            Solution solution;
            if (sdkPaths.Count > 0)
            {
                // SDK-style projects: MSBuildWorkspace, which loads them in the .NET SDK's build host.
                var msbuildWorkspace = await msbuild.CreateAsync(target, properties, cancellationToken);
                workspace = msbuildWorkspace;
                var progress = new Progress(p =>
                {
                    if (!string.IsNullOrEmpty(p.TargetFramework))
                    {
                        frameworks.GetOrAdd(p.FilePath, _ => new(StringComparer.OrdinalIgnoreCase)).TryAdd(p.TargetFramework, 0);
                    }

                    onProgress?.Invoke(p);
                });
                foreach (var path in sdkPaths)
                {
                    if (msbuildWorkspace.CurrentSolution.Projects.Any(p => string.Equals(p.FilePath, path, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    try
                    {
                        await msbuildWorkspace.OpenProjectAsync(path, progress, cancellationToken);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or IOException or XmlException)
                    {
                        issues.Add(new LoadIssue("error", $"{path}: {ex.Message}"));
                    }
                }

                solution = msbuildWorkspace.CurrentSolution;

                // A classic project an SDK-style one references may have loaded (or failed to) through MSBuild: the heuristic
                // reading replaces it, so its MSBuild messages do not apply.
                issues.AddRange(MSBuildWorkspaceLoader.IssuesOf(msbuildWorkspace, solution)
                    .Where(i => !classicSet.Any(c => i.Message.Contains(c, StringComparison.OrdinalIgnoreCase))));
            }
            else
            {
                workspace = new AdhocWorkspace();
                solution = workspace.CurrentSolution;
            }

            var replaced = new Dictionary<ProjectId, string>();
            foreach (var project in solution.Projects.Where(p => p.FilePath is { } f && classicSet.Contains(f)).ToList())
            {
                replaced[project.Id] = project.FilePath!;
                solution = solution.RemoveProject(project.Id);
            }

            var nuget = NuGetContext.Load(target.Directory);
            var references = new FrameworkReferenceAssemblies(feeds, nuget);
            var ids = classic.ToDictionary(c => c.Path, _ => ProjectId.CreateNewId(), StringComparer.OrdinalIgnoreCase);
            var metadata = new ConcurrentDictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);
            foreach (var project in classic)
            {
                frameworks.GetOrAdd(project.Path, _ => new(StringComparer.OrdinalIgnoreCase)).TryAdd(ProjectFileInspector.ToShortFrameworkName(project.TargetFrameworkVersion), 0);
                solution = solution.AddProject(await InfoAsync(project, ids[project.Path], ids, solution, references, metadata, issues, cancellationToken));
            }

            solution = WireSdkReferences(solution, classic, ids, replaced);
            issues.AddRange(references.Missing.Distinct(StringComparer.Ordinal).Select(m => new LoadIssue("error", $"Missing for the heuristic load: {m}.")));
            foreach (var project in classic.Where(c => c.HasXaml))
            {
                issues.Add(new LoadIssue("warning", $"{project.Name}: XAML code-behind parts (InitializeComponent, named elements) are generated by MSBuild targets, which a heuristic load does not run; errors about them were there at load."));
            }

            (solution, var prepared) = await MSBuildWorkspaceLoader.PrepareAsync(solution, target.Directory, cancellationToken);
            issues.AddRange(prepared);
            issues.Insert(0, new LoadIssue(
                "warning",
                "Heuristic load: no MSBuild here can evaluate .NET Framework projects, so their project files were read directly. Imports and " +
                "targets did not run; dotnet_build and dotnet_test need MSBuild (Visual Studio or Build Tools on Windows)."));
            stopwatch.Stop();

            var report = new LoadReport(
                Name,
                stopwatch.Elapsed,
                ProjectFiles: solution.Projects.Select(p => p.FilePath).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                RoslynProjects: solution.ProjectIds.Count,
                Documents: solution.Projects.SelectMany(p => p.Documents).Select(d => d.FilePath ?? d.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                TargetFrameworks: frameworks.ToDictionary(
                    kv => kv.Key,
                    kv => (IReadOnlyList<string>)[.. kv.Value.Keys.Order(StringComparer.OrdinalIgnoreCase)],
                    StringComparer.OrdinalIgnoreCase),
                Issues: [.. issues.Distinct()]);
            LogLoaded(logger, target.Path, classic.Count, sdkPaths.Count, report.Issues.Count, report.Elapsed);
            return new LoadedWorkspace(workspace, solution, report);
        }
        catch
        {
            workspace?.Dispose();
            throw;
        }
    }

    // The project files SDK-style projects reference, transitively through SDK-style ones.
    private static List<string> SdkClosure(List<string> sdkPaths)
    {
        var seen = new HashSet<string>(sdkPaths, StringComparer.OrdinalIgnoreCase);
        var found = new List<string>();
        var queue = new Queue<string>(sdkPaths);
        while (queue.TryDequeue(out var path))
        {
            foreach (var reference in SdkProjectReferences(path, evaluate: false, targetFramework: null).Where(r => File.Exists(r) && seen.Add(r)))
            {
                found.Add(reference);
                if (ProjectFileInspector.Inspect(reference).IsSdkStyle)
                {
                    queue.Enqueue(reference);
                }
            }
        }

        return found;
    }

    // The classic projects listed, plus the classic ones they reference that the solution does not list.
    private static async Task<List<ClassicProjectFile>> ReadClassicAsync(HashSet<string> paths, WorkspaceTarget target, List<LoadIssue> issues, CancellationToken cancellationToken)
    {
        var solutionDirectory = target.Kind == WorkspaceTargetKind.Project ? null : target.Directory;
        var read = new Dictionary<string, ClassicProjectFile>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(paths);
        while (queue.TryDequeue(out var path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (read.ContainsKey(path))
            {
                continue;
            }

            try
            {
                var project = await Task.Run(() => ClassicProjectFile.Read(path, solutionDirectory), cancellationToken);
                read[path] = project;
                foreach (var reference in project.ProjectReferences.Where(r => File.Exists(r) && !read.ContainsKey(r) && !ProjectFileInspector.Inspect(r).IsSdkStyle))
                {
                    paths.Add(reference);
                    queue.Enqueue(reference);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or ArgumentException or NotSupportedException)
            {
                issues.Add(new LoadIssue("error", $"{path}: the project file could not be read ({ex.Message})."));
            }
        }

        return [.. read.Values];
    }

    private static async Task<ProjectInfo> InfoAsync(
        ClassicProjectFile project,
        ProjectId id,
        Dictionary<string, ProjectId> classicIds,
        Solution solution,
        FrameworkReferenceAssemblies references,
        ConcurrentDictionary<string, MetadataReference> metadata,
        List<LoadIssue> issues,
        CancellationToken cancellationToken)
    {
        var framework = await references.FrameworkAsync(project.TargetFrameworkVersion, cancellationToken);
        var paths = new List<string>();
        var missing = new List<string>();

        // MSBuild adds System.Core to projects for .NET Framework 3.5 and later (AddAdditionalExplicitAssemblyReferences).
        var assemblyReferences = project.References.ToList();
        if (Version.TryParse(project.TargetFrameworkVersion.TrimStart('v', 'V'), out var version) && version >= new Version(3, 5)
            && !assemblyReferences.Any(r => string.Equals(r.Name, "System.Core", StringComparison.OrdinalIgnoreCase)))
        {
            assemblyReferences.Add(new ClassicReference("System.Core", HintPath: null));
        }

        foreach (var reference in assemblyReferences)
        {
            if (await ResolveAsync(project, reference, framework, references, cancellationToken) is { } path)
            {
                paths.Add(path);
            }
            else
            {
                missing.Add(reference.Name);
            }
        }

        if (project.PackageReferences.Count > 0)
        {
            var targetFramework = NuGetFramework.Parse(ProjectFileInspector.ToShortFrameworkName(project.TargetFrameworkVersion));
            foreach (var package in project.PackageReferences)
            {
                if (await references.PackageAsync(package, "PackageReference", cancellationToken) is { } folder
                    && PackageApi.SelectAssemblies(folder, targetFramework) is { } assemblies)
                {
                    // First: a package's assembly wins over a framework assembly a Reference without HintPath named.
                    paths.InsertRange(0, assemblies.Files);
                }
            }

            issues.Add(new LoadIssue("warning", $"{project.Name}: its PackageReference packages were resolved without their dependencies; a heuristic load does not restore."));
        }

        // The framework last: an assembly a HintPath or a package brings wins over the framework's copy (a facade, say).
        foreach (var folder in framework)
        {
            paths.Add(Path.Combine(folder, "mscorlib.dll"));
        }

        foreach (var facades in framework.Select(f => PathCase.Directory(Path.Combine(f, "Facades"))).OfType<string>())
        {
            paths.AddRange(Directory.EnumerateFiles(facades, "*.dll"));
        }

        if (missing.Count > 0)
        {
            issues.Add(new LoadIssue("warning", $"{project.Name}: references not found by the heuristic load: {string.Join(", ", missing.Order(StringComparer.OrdinalIgnoreCase))}."));
        }

        var analyzers = new List<string>();
        foreach (var analyzer in project.Analyzers)
        {
            analyzers.Add(File.Exists(analyzer) ? analyzer : await FromPackagesAsync(project, analyzer, references, cancellationToken) ?? analyzer);
        }

        var vbRuntime = framework.Select(f => PathCase.File(Path.Combine(f, "Microsoft.VisualBasic.dll"))).OfType<string>().FirstOrDefault();
        var (compilationOptions, parseOptions, errors) = Options(project, vbRuntime);
        if (errors.Count > 0)
        {
            issues.Add(new LoadIssue("warning", $"{project.Name}: compiler options the heuristic load could not read: {string.Join("; ", errors)}."));
        }

        if (project.Language == LanguageNames.VisualBasic && vbRuntime is not null)
        {
            paths.Add(vbRuntime);
        }

        var projectReferences = new List<ProjectReference>();
        foreach (var path in project.ProjectReferences)
        {
            if (classicIds.TryGetValue(path, out var classicId))
            {
                projectReferences.Add(new ProjectReference(classicId));
            }
            else if (SdkProject(solution, path, NuGetFramework.Parse(ProjectFileInspector.ToShortFrameworkName(project.TargetFrameworkVersion))) is { } sdk)
            {
                projectReferences.Add(new ProjectReference(sdk.Id));
            }
            else
            {
                issues.Add(new LoadIssue("warning", $"{project.Name}: referenced project not loaded: {path}."));
            }
        }

        var analyzerLoader = new CopiedAnalyzerLoader(analyzers.Where(File.Exists), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var outputDirectory = project.Property("OutputPath") is { } output ? Path.GetFullPath(Path.Combine(project.Directory, output.Replace('\\', Path.DirectorySeparatorChar))) : Path.Combine(project.Directory, "bin", "Debug");
        var extension = project.OutputType.Contains("exe", StringComparison.OrdinalIgnoreCase) ? ".exe" : ".dll";
        var documents = project.Compile
            .Where(c => File.Exists(c.Path))
            .Select(c => DocumentInfo.Create(DocumentId.CreateNewId(id, c.Path), Path.GetFileName(c.Path), c.Folders, SourceCodeKind.Regular, new FileTextLoader(c.Path, defaultEncoding: null), c.Path))
            .ToList();
        var absent = project.Compile.Where(c => !File.Exists(c.Path)).Select(c => c.Path).ToList();
        if (absent.Count > 0)
        {
            issues.Add(new LoadIssue("warning", $"{project.Name}: compiled files that do not exist: {string.Join(", ", absent.Take(5))}{(absent.Count > 5 ? $" and {absent.Count - 5} more" : string.Empty)}."));
        }

        return ProjectInfo.Create(
            id,
            VersionStamp.Create(),
            project.Name,
            project.AssemblyName,
            project.Language,
            filePath: project.Path,
            outputFilePath: Path.Combine(outputDirectory, project.AssemblyName + extension),
            compilationOptions: compilationOptions,
            parseOptions: parseOptions,
            documents: documents,
            projectReferences: projectReferences,
            metadataReferences: [.. paths.Where(File.Exists).DistinctBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase).Select(p => metadata.GetOrAdd(p, Reference))],
            analyzerReferences: [.. analyzers.Distinct(StringComparer.OrdinalIgnoreCase).Select(a => File.Exists(a) ? new AnalyzerFileReference(a, analyzerLoader) : (AnalyzerReference)new UnresolvedAnalyzerReference(a))])
            .WithAnalyzerConfigDocuments(EditorConfigs(id, project.Directory));
    }

    private static MetadataReference Reference(string path)
    {
        var xml = Path.ChangeExtension(path, ".xml");
        return MetadataReference.CreateFromFile(path, documentation: File.Exists(xml) ? XmlDocumentationProvider.CreateFromFile(xml) : null);
    }

    // A HintPath that exists; one into a packages.config folder that is not restored, mapped to the global packages
    // folder; else the framework assembly of that name.
    private static async Task<string?> ResolveAsync(ClassicProjectFile project, ClassicReference reference, IReadOnlyList<string> framework, FrameworkReferenceAssemblies references, CancellationToken cancellationToken)
    {
        if (reference.HintPath is { } hint)
        {
            if (File.Exists(hint))
            {
                return hint;
            }

            if (await FromPackagesAsync(project, hint, references, cancellationToken) is { } fromCache && File.Exists(fromCache))
            {
                return fromCache;
            }
        }

        return framework.Select(f => PathCase.File(Path.Combine(f, reference.Name + ".dll"))).OfType<string>().FirstOrDefault()
            ?? framework.Select(f => PathCase.File(Path.Combine(f, "Facades", reference.Name + ".dll"))).OfType<string>().FirstOrDefault();
    }

    // "…\packages\Id.1.2.3\lib\net45\X.dll" → "<global packages>/id/1.2.3/lib/net45/X.dll", for a package of packages.config.
    private static async Task<string?> FromPackagesAsync(ClassicProjectFile project, string path, FrameworkReferenceAssemblies references, CancellationToken cancellationToken)
    {
        var parts = path.Replace('\\', '/').Split('/');
        foreach (var package in project.Packages)
        {
            if (!NuGetVersion.TryParse(package.Version, out var version))
            {
                continue;
            }

            // "Newtonsoft.Json.13.0.1", or the folder of a version written another way ("1.0" for 1.0.0).
            var index = Array.FindIndex(parts, p => p.StartsWith(package.Id + ".", StringComparison.OrdinalIgnoreCase)
                && NuGetVersion.TryParse(p[(package.Id.Length + 1)..], out var folderVersion) && folderVersion == version);
            if (index >= 0 && await references.PackageAsync(package, "packages.config", cancellationToken) is { } folder)
            {
                var inPackage = Path.Combine([folder, .. parts.Skip(index + 1)]);
                return PathCase.File(inPackage) ?? inPackage;
            }
        }

        return null;
    }

    // The variant of an SDK-style project a classic one compiles against: the nearest compatible target framework, as
    // NuGet picks it; else (no variant is compatible, or the names carry none) a net4*, then netstandard one.
    private static Project? SdkProject(Solution solution, string path, NuGetFramework classicFramework)
    {
        var variants = solution.Projects.Where(p => string.Equals(p.FilePath, path, StringComparison.OrdinalIgnoreCase)).ToList();
        var withFramework = variants.Where(p => TargetFrameworkOf(p.Name) is not null).ToList();
        return NuGetFrameworkUtility.GetNearest(withFramework, classicFramework, p => NuGetFramework.Parse(TargetFrameworkOf(p.Name)!))
            ?? variants
                .OrderBy(p => p.Name.Contains("(net4", StringComparison.OrdinalIgnoreCase) ? 0 : p.Name.Contains("(netstandard", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                .ThenByDescending(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
    }

    // SDK-style projects that reference a classic one: point them at the heuristic project, in place of whatever MSBuild
    // gave them (a project that is now gone, or the classic project's built assembly).
    private static Solution WireSdkReferences(Solution solution, List<ClassicProjectFile> classic, Dictionary<string, ProjectId> ids, Dictionary<ProjectId, string> replaced)
    {
        var assemblyOf = classic.ToDictionary(c => ids[c.Path], c => c.AssemblyName);
        foreach (var project in solution.Projects.Where(p => !ids.ContainsValue(p.Id)).ToList())
        {
            var current = solution.GetProject(project.Id)!;
            var wanted = SdkProjectReferences(current.FilePath, evaluate: true, TargetFrameworkOf(current.Name)).Where(ids.ContainsKey).Select(p => ids[p]).ToHashSet();
            wanted.UnionWith(current.AllProjectReferences.Where(r => replaced.ContainsKey(r.ProjectId)).Select(r => ids.GetValueOrDefault(replaced[r.ProjectId])).OfType<ProjectId>());
            if (wanted.Count == 0)
            {
                continue;
            }

            var kept = current.AllProjectReferences.Where(r => solution.ContainsProject(r.ProjectId) && !wanted.Contains(r.ProjectId));
            var wantedAssemblies = wanted.Select(w => assemblyOf[w]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var metadata = current.MetadataReferences.Where(m => m is not PortableExecutableReference { FilePath: { } file } || !wantedAssemblies.Contains(Path.GetFileNameWithoutExtension(file)));
            solution = solution
                .WithProjectReferences(project.Id, [.. kept, .. wanted.Select(w => new ProjectReference(w))])
                .WithProjectMetadataReferences(project.Id, metadata);
        }

        return solution;
    }

    // "Modern.Core(net48)" → net48; a single-target project's name has none.
    private static string? TargetFrameworkOf(string name) =>
        name.EndsWith(')') && name.LastIndexOf('(') is var open and > 0 ? name[(open + 1)..^1] : null;

    // An SDK-style project's ProjectReference items. With evaluate, their conditions (and Choose branches) are evaluated
    // for one target framework: the variant's, or the project's own TargetFramework when it has one; without, every
    // reference counts, conditions or not (to find the projects a load may need).
    private static List<string> SdkProjectReferences(string? path, bool evaluate, string? targetFramework)
    {
        if (path is null || !File.Exists(path))
        {
            return [];
        }

        try
        {
            var directory = Path.GetDirectoryName(path)!;
            var root = XDocument.Load(path).Root;
            if (root is null)
            {
                return [];
            }

            var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Configuration"] = "Debug",
                ["Platform"] = "AnyCPU",
                ["MSBuildProjectDirectory"] = directory,
                ["MSBuildThisFileDirectory"] = directory + Path.DirectorySeparatorChar,
                ["ProjectDir"] = directory + Path.DirectorySeparatorChar,
                ["MSBuildProjectName"] = Path.GetFileNameWithoutExtension(path),
            };
            var evaluator = new ConditionEvaluator(properties, directory);
            foreach (var property in root.Elements().Where(e => e.Name.LocalName == "PropertyGroup" && evaluator.Applies(e)).SelectMany(g => g.Elements()).Where(evaluator.Applies))
            {
                properties[property.Name.LocalName] = evaluator.Expand(property.Value.Trim());
            }

            if (targetFramework is not null)
            {
                properties["TargetFramework"] = targetFramework;
            }

            if (properties.GetValueOrDefault("TargetFramework") is { Length: > 0 } tfm)
            {
                var parsed = NuGetFramework.Parse(tfm);
                properties["TargetFrameworkIdentifier"] = parsed.Framework;
                properties["TargetFrameworkVersion"] = $"v{parsed.Version.ToString(parsed.Version.Build > 0 ? 3 : 2)}";
            }

            return [.. root.Descendants().Where(e => e.Name.LocalName == "ProjectReference")
                .Where(e => !evaluate || (e.AncestorsAndSelf().All(evaluator.Applies) && e.Ancestors().All(a => Chosen(a, evaluator))))
                .Select(e => (string?)e.Attribute("Include"))
                .OfType<string>()
                .SelectMany(i => evaluator.Expand(i).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Where(i => !i.Contains("$(", StringComparison.Ordinal) && !i.Contains("@(", StringComparison.Ordinal))
                .Select(i => ClassicProjectFile.Full(directory, i))
                .Select(p => PathCase.File(p) ?? p)];
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }

    // A <When> is taken when it holds and no <When> before it did; <Otherwise> when none did. Other elements: true.
    private static bool Chosen(XElement element, ConditionEvaluator evaluator) => element.Name.LocalName switch
    {
        "When" => evaluator.Applies(element) && !element.ElementsBeforeSelf().Any(w => w.Name.LocalName == "When" && evaluator.Applies(w)),
        "Otherwise" => !element.ElementsBeforeSelf().Any(w => w.Name.LocalName == "When" && evaluator.Applies(w)),
        _ => true,
    };

    // The .editorconfig files from the project's folder up, nearest first, until one says root = true: what MSBuild passes.
    private static List<DocumentInfo> EditorConfigs(ProjectId id, string directory)
    {
        var configs = new List<DocumentInfo>();
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            var file = Path.Combine(current.FullName, ".editorconfig");
            if (!File.Exists(file))
            {
                continue;
            }

            configs.Add(DocumentInfo.Create(DocumentId.CreateNewId(id, file), ".editorconfig", loader: new FileTextLoader(file, defaultEncoding: null), filePath: file));
            try
            {
                if (File.ReadLines(file).Any(l => l.Replace(" ", string.Empty, StringComparison.Ordinal).Equals("root=true", StringComparison.OrdinalIgnoreCase)))
                {
                    break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                break;
            }
        }

        return configs;
    }

    /// <summary>The project's compiler options, built as csc and vbc arguments from its properties and parsed by Roslyn's command-line parsers.</summary>
    private static (CompilationOptions Compilation, ParseOptions Parse, List<string> Errors) Options(ClassicProjectFile project, string? vbRuntime)
    {
        var target = project.OutputType.ToUpperInvariant() switch
        {
            "EXE" => "exe",
            "WINEXE" => "winexe",
            "MODULE" => "module",
            "WINMDOBJ" => "winmdobj",
            "APPCONTAINEREXE" => "appcontainerexe",
            _ => "library",
        };
        var args = new List<string> { "/nostdlib+", $"/target:{target}" };
        if (project.Property("NoWarn") is { } noWarn)
        {
            args.Add($"/nowarn:{noWarn.Replace(';', ',')}");
        }

        if (string.Equals(project.Property("TreatWarningsAsErrors"), "true", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("/warnaserror+");
        }

        if (project.Property("WarningsAsErrors") is { } warningsAsErrors)
        {
            args.Add($"/warnaserror+:{warningsAsErrors.Replace(';', ',')}");
        }

        if (project.Property("WarningsNotAsErrors") is { } warningsNotAsErrors)
        {
            args.Add($"/warnaserror-:{warningsNotAsErrors.Replace(';', ',')}");
        }

        if (project.Property("LangVersion") is { } langVersion)
        {
            args.Add($"/langversion:{langVersion}");
        }

        if (project.Language == LanguageNames.VisualBasic)
        {
            if (project.Property("RootNamespace") is { } rootNamespace)
            {
                args.Add($"/rootnamespace:{rootNamespace}");
            }

            if (project.Imports.Count > 0)
            {
                args.Add($"/imports:{string.Join(',', project.Imports)}");
            }

            AddSwitch(args, "optionstrict", project.Property("OptionStrict"));
            AddSwitch(args, "optionexplicit", project.Property("OptionExplicit"));
            AddSwitch(args, "optioninfer", project.Property("OptionInfer"));
            if (project.Property("OptionCompare") is { } compare)
            {
                args.Add($"/optioncompare:{compare.ToLowerInvariant()}");
            }

            if (vbRuntime is not null)
            {
                args.Add($"/vbruntime:{vbRuntime}");
            }

            // What Microsoft.VisualBasic.targets passes as FinalDefineConstants for the configuration read.
            var defines = new List<string> { $"CONFIG=\"{project.Configuration}\"" };
            if (string.Equals(project.Property("DefineDebug"), "true", StringComparison.OrdinalIgnoreCase))
            {
                defines.Add("DEBUG=-1");
            }

            if (string.Equals(project.Property("DefineTrace"), "true", StringComparison.OrdinalIgnoreCase))
            {
                defines.Add("TRACE=-1");
            }

            var myType = project.Property("MyType") ?? project.OutputType.ToUpperInvariant() switch
            {
                "EXE" => "Console",
                "WINEXE" => "WindowsForms",
                "MODULE" => "Empty",
                _ => "Windows",
            };
            defines.Add($"_MyType=\"{myType}\"");
            defines.Add($"PLATFORM=\"{project.Platform.Replace(" ", string.Empty, StringComparison.Ordinal)}\"");
            if (project.Property("DefineConstants") is { } extra)
            {
                defines.Add(extra);
            }

            args.Add($"/define:{string.Join(',', defines)}");
            var vb = VisualBasicCommandLineParser.Default.Parse(args, project.Directory, sdkDirectory: null);
            return (vb.CompilationOptions.WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default), vb.ParseOptions, Errors(vb.Errors));
        }

        if (project.Property("DefineConstants") is { } constants)
        {
            var symbols = constants.Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (symbols.Length > 0)
            {
                args.Add($"/define:{string.Join(';', symbols)}");
            }
        }

        if (project.Property("StartupObject") is { } startup && target is "exe" or "winexe")
        {
            args.Add($"/main:{startup}");
        }

        if (string.Equals(project.Property("AllowUnsafeBlocks"), "true", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("/unsafe+");
        }

        if (project.Property("Nullable") is { } nullable)
        {
            args.Add($"/nullable:{nullable.ToLowerInvariant()}");
        }

        if (string.Equals(project.Property("CheckForOverflowUnderflow"), "true", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("/checked+");
        }

        // .NET Framework projects without LangVersion build with C# 7.3, the SDK's default for them.
        if (project.Property("LangVersion") is null)
        {
            args.Add("/langversion:7.3");
        }

        var cs = CSharpCommandLineParser.Default.Parse(args, project.Directory, sdkDirectory: null);
        return (cs.CompilationOptions.WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default), cs.ParseOptions, Errors(cs.Errors));
    }

    private static void AddSwitch(List<string> args, string name, string? value)
    {
        if (string.Equals(value, "On", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
        {
            args.Add($"/{name}+");
        }
        else if (string.Equals(value, "Off", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
        {
            args.Add($"/{name}-");
        }
    }

    // Errors only: warnings about switches (an unknown /langversion, say) do not change the options.
    private static List<string> Errors(IEnumerable<Diagnostic> diagnostics) =>
        [.. diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture))];

    [LoggerMessage(Level = LogLevel.Information, Message = "Heuristic load of {Workspace}: {Classic} classic project(s) read directly, {Sdk} SDK-style project(s) through MSBuild, {Issues} issue(s) in {Elapsed}")]
    private static partial void LogLoaded(ILogger logger, string workspace, int classic, int sdk, int issues, TimeSpan elapsed);

    private sealed class Progress(Action<ProjectLoadProgress> handler) : IProgress<ProjectLoadProgress>
    {
        public void Report(ProjectLoadProgress value) => handler(value);
    }
}
