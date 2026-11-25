using System.Diagnostics;
using Basic.CompilerLog.Util;
using Farol.Core.Paths;
using Farol.Engine.Workspaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Farol.Engine.Loading;

/// <summary>
/// Loads a workspace from an MSBuild binary log (<c>-bl</c>): every compiler call the build recorded becomes a project,
/// with the exact files, references, options and project references the compiler got (Basic.CompilerLog). No MSBuild
/// runs, so it works on any OS where the files the log names exist. Analyzers and source generators are not loaded;
/// generated files the log recorded are added as they were.
/// </summary>
public sealed partial class BinlogWorkspaceLoader(PathSandbox paths, ILogger<BinlogWorkspaceLoader> logger)
{
    public const string Name = "binlog";

    // What vbc resolves from /sdkpath on its own: the log's arguments do not list them, so a replay misses the core library.
    private static readonly string[] VisualBasicSdkAssemblies = ["mscorlib.dll", "Microsoft.VisualBasic.dll"];

    public async Task<LoadedWorkspace> LoadAsync(WorkspaceTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var stopwatch = Stopwatch.StartNew();
        var issues = new List<LoadIssue>();
        var (info, frameworks, sdkPaths) = await Task.Run(() => Read(target.Path, cancellationToken), cancellationToken);
        if (info.Projects.Count == 0)
        {
            throw new InvalidOperationException(
                $"{target.DisplayName} records no compiler call: build with -bl and a rebuild (an up-to-date build skips the compiler), then name the new log.");
        }

        var workspace = new AdhocWorkspace();
        try
        {
            // The log names the files Farol then shows: sources outside the root and trusted paths are left out.
            var (trusted, outside) = WithinTrust(Normalize(info, cancellationToken));
            if (outside.Count > 0)
            {
                issues.Add(new LoadIssue("warning", $"{outside.Count} source file(s) the log names are outside the root and Farol:TrustedPaths and were left out, e.g. {outside[0]}."));
            }

            var solution = workspace.AddSolution(trusted);
            solution = WithVisualBasicSdkReferences(solution, sdkPaths);
            (solution, var prepared) = await MSBuildWorkspaceLoader.PrepareAsync(solution, target.Directory, cancellationToken);
            issues.AddRange(prepared);

            var logTime = File.GetLastWriteTimeUtc(target.Path);
            var newer = solution.Projects.SelectMany(p => p.Documents).Select(d => d.FilePath).OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(f => File.Exists(f) && File.GetLastWriteTimeUtc(f) > logTime);
            issues.Insert(0, new LoadIssue(
                "warning",
                $"Binary-log load: the workspace is what the build in {target.DisplayName} compiled; MSBuild did not run, and project analyzers are not loaded." +
                (newer > 0 ? $" The log is older than {newer} source file(s): it may not match the projects as they are now." : string.Empty)));
            stopwatch.Stop();

            var report = new LoadReport(
                Name,
                stopwatch.Elapsed,
                ProjectFiles: solution.Projects.Select(p => p.FilePath).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                RoslynProjects: solution.ProjectIds.Count,
                Documents: solution.Projects.SelectMany(p => p.Documents).Select(d => d.FilePath ?? d.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                TargetFrameworks: frameworks,
                Issues: [.. issues.Distinct()]);
            LogLoaded(logger, target.Path, report.RoslynProjects, report.Documents, newer, report.Elapsed);
            return new LoadedWorkspace(workspace, solution, report);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    private static (SolutionInfo Info, Dictionary<string, IReadOnlyList<string>> Frameworks, Dictionary<string, string> SdkPaths) Read(string binlog, CancellationToken cancellationToken)
    {
        static bool Compiled(CompilerCall call) => call.Kind == CompilerCallKind.Regular;

        var frameworks = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var sdkPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = BinaryLogReader.Create(binlog, BasicAnalyzerKind.None))
        {
            foreach (var call in reader.ReadAllCompilerCalls(Compiled))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (call.TargetFramework is { Length: > 0 } framework)
                {
                    frameworks[call.ProjectFilePath] = [.. (frameworks.GetValueOrDefault(call.ProjectFilePath) ?? []).Append(framework).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
                }

                if (call.IsVisualBasic && reader.ReadArguments(call).LastOrDefault(a => a.StartsWith("/sdkpath:", StringComparison.OrdinalIgnoreCase)) is { } sdkPath)
                {
                    sdkPaths[call.ProjectFilePath] = sdkPath["/sdkpath:".Length..].Trim('"');
                }
            }
        }

        using var solutionReader = SolutionReader.Create(binlog, BasicAnalyzerKind.None, predicate: Compiled);
        return (solutionReader.ReadSolutionInfo(), frameworks, sdkPaths);
    }

    /// <summary>
    /// The solution as Farol's other loaders shape it: projects named after their file (with the target framework when
    /// a file compiled more than once), documents named after theirs with their folders and read from disk, fresh
    /// versions (Basic.CompilerLog's are not distinct, and a cross-language reference then misses its project), and the
    /// generated files the log recorded as documents: they come as an analyzer reference of Basic.CompilerLog's own type,
    /// which a workspace cannot hold (it checksums file references only).
    /// </summary>
    private static SolutionInfo Normalize(SolutionInfo info, CancellationToken cancellationToken)
    {
        var projects = new List<ProjectInfo>();
        var calls = info.Projects.Where(p => p.FilePath is not null).GroupBy(p => p.FilePath!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (var project in info.Projects)
        {
            var directory = project.FilePath is null ? null : Path.GetDirectoryName(project.FilePath);
            var documents = project.Documents.Select(d => Shaped(d, directory)).ToList();
            var replay = project.AnalyzerReferences.Where(r => r is not AnalyzerFileReference).ToList();
            var generators = replay.SelectMany(r => r.GetGenerators(project.Language)).ToList();
            if (generators.Count > 0 && project.ParseOptions is not null)
            {
                var vb = project.Language == LanguageNames.VisualBasic;
                var empty = vb
                    ? (Compilation)Microsoft.CodeAnalysis.VisualBasic.VisualBasicCompilation.Create(project.AssemblyName)
                    : Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(project.AssemblyName);
                GeneratorDriver driver = vb
                    ? Microsoft.CodeAnalysis.VisualBasic.VisualBasicGeneratorDriver.Create([.. generators], parseOptions: (Microsoft.CodeAnalysis.VisualBasic.VisualBasicParseOptions)project.ParseOptions)
                    : Microsoft.CodeAnalysis.CSharp.CSharpGeneratorDriver.Create(generators, parseOptions: (Microsoft.CodeAnalysis.CSharp.CSharpParseOptions)project.ParseOptions);
                foreach (var tree in driver.RunGenerators(empty, cancellationToken).GetRunResult().GeneratedTrees)
                {
                    documents.Add(DocumentInfo.Create(
                        DocumentId.CreateNewId(project.Id, tree.FilePath),
                        Path.GetFileName(tree.FilePath),
                        loader: TextLoader.From(TextAndVersion.Create(tree.GetText(cancellationToken), VersionStamp.Create(), tree.FilePath)),
                        filePath: tree.FilePath));
                }
            }

            var name = project.FilePath is null ? project.Name : Path.GetFileNameWithoutExtension(project.FilePath);
            if (project.FilePath is not null && calls[project.FilePath] > 1 && project.Name.LastIndexOf('(') is var open and > 0 && project.Name.EndsWith(')'))
            {
                name += project.Name[open..];
            }

            var taken = projects.Count(p => p.Name == name || p.Name.StartsWith(name + " #", StringComparison.Ordinal));
            if (taken > 0)
            {
                name += $" #{taken + 1}";
            }

            projects.Add(project
                .WithName(name)
                .WithVersion(VersionStamp.Create())
                .WithAnalyzerReferences(project.AnalyzerReferences.Except(replay))
                .WithDocuments(documents)
                .WithAdditionalDocuments(project.AdditionalDocuments.Select(d => Shaped(d, directory)))
                .WithAnalyzerConfigDocuments(project.AnalyzerConfigDocuments.Select(d => Shaped(d, directory))));
        }

        return SolutionInfo.Create(info.Id, VersionStamp.Create(), info.FilePath, projects, info.AnalyzerReferences.Where(r => r is AnalyzerFileReference));
    }

    private (SolutionInfo Info, List<string> Outside) WithinTrust(SolutionInfo info)
    {
        var outside = new List<string>();
        bool Keep(DocumentInfo d)
        {
            if (d.FilePath is null || paths.Contains(Path.GetFullPath(d.FilePath)))
            {
                return true;
            }

            outside.Add(d.FilePath);
            return false;
        }

        var projects = info.Projects.Select(p => p
            .WithDocuments(p.Documents.Where(Keep))
            .WithAdditionalDocuments(p.AdditionalDocuments.Where(Keep))).ToList();
        return (SolutionInfo.Create(info.Id, info.Version, info.FilePath, projects, info.AnalyzerReferences), outside);
    }

    // A document read from its file when it exists, named after it, under the folders between the project and it.
    private static DocumentInfo Shaped(DocumentInfo document, string? projectDirectory)
    {
        if (document.FilePath is not { } path)
        {
            return document;
        }

        var relative = projectDirectory is null ? null : Path.GetRelativePath(projectDirectory, path);
        var folders = relative is null || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
            ? []
            : relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[..^1];
        var shaped = document.WithName(Path.GetFileName(path)).WithFolders(folders);
        return File.Exists(path) ? shaped.WithTextLoader(new FileTextLoader(path, defaultEncoding: null)) : shaped;
    }

    private static Solution WithVisualBasicSdkReferences(Solution solution, Dictionary<string, string> sdkPaths)
    {
        foreach (var project in solution.Projects.Where(p => p.Language == LanguageNames.VisualBasic && p.FilePath is not null).ToList())
        {
            if (!sdkPaths.TryGetValue(project.FilePath!, out var sdkPath))
            {
                continue;
            }

            var present = project.MetadataReferences.OfType<PortableExecutableReference>().Select(r => Path.GetFileName(r.FilePath)).OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = VisualBasicSdkAssemblies.Where(a => !present.Contains(a)).Select(a => Path.Combine(sdkPath, a)).Where(File.Exists)
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
            if (missing.Count > 0)
            {
                solution = solution.AddMetadataReferences(project.Id, missing);
            }
        }

        return solution;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Binary-log load of {Workspace}: {Projects} compilation(s), {Documents} document(s), {Newer} file(s) newer than the log, in {Elapsed}")]
    private static partial void LogLoaded(ILogger logger, string workspace, int projects, int documents, int newer, TimeSpan elapsed);
}
