using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Farol.Engine.Workspaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Farol.Engine.Loading;

/// <summary>
/// Roslyn loads analyzers and source generators into Farol's process, and the process then holds their files until it
/// exits: a solution that builds its own generators could no longer replace them (AC-47). Each analyzer is loaded from a
/// copy instead, under the temp directory, in one folder per process, which a later server removes once this process has exited.
/// </summary>
internal static class AnalyzerCopies
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "farol", "analyzers");

    private static readonly Lazy<string> ProcessDirectory = new(CreateDirectory);

    // One loader per set of copies: a reload that copies nothing new runs the assemblies already loaded.
    private static readonly ConcurrentDictionary<string, CopiedAnalyzerLoader> Loaders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The solution with every analyzer file reference pointing at a copy, loaded by a <see cref="CopiedAnalyzerLoader"/>
    /// that knows only these copies. The analyzers of one folder are copied together into a folder named after their sizes
    /// and times: a rebuilt generator gets a new folder, since the copy already loaded cannot be replaced either. A folder
    /// that cannot be copied loads from its own files, and a load issue says so.
    /// </summary>
    public static (Solution Solution, List<LoadIssue> Issues) WithCopies(Solution solution)
    {
        var paths = solution.Projects
            .SelectMany(p => p.AnalyzerReferences.OfType<AnalyzerFileReference>())
            .Select(r => r.FullPath)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (paths.Count == 0)
        {
            return (solution, []);
        }

        var copies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var issues = new List<LoadIssue>();
        foreach (var folder in paths.GroupBy(p => Path.GetDirectoryName(p)!, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var files = folder.Order(StringComparer.OrdinalIgnoreCase).Select(path => (Path: path, Info: new FileInfo(path))).ToList();
                var target = Path.Combine(ProcessDirectory.Value, FolderName(folder.Key, files.Select(f => f.Info)));
                Directory.CreateDirectory(target);
                foreach (var (path, info) in files)
                {
                    var copy = Path.Combine(target, info.Name);
                    if (!File.Exists(copy))
                    {
                        CopyOnce(info, copy);
                    }

                    copies[path] = copy;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or SecurityException)
            {
                // A file a build is writing, or a temp directory Farol cannot write to.
                foreach (var path in folder)
                {
                    copies[path] = path;
                }

                issues.Add(new LoadIssue(
                    "warning",
                    $"Farol could not copy the analyzers in {folder.Key} ({ex.Message}): it loads them from their own files, and holds those " +
                    "until it exits, so rebuilding them fails while it runs."));
            }
        }

        var loader = Loaders.GetOrAdd(string.Join('|', copies.Values.Order(StringComparer.OrdinalIgnoreCase)), _ => new CopiedAnalyzerLoader(copies.Values));
        foreach (var project in solution.Projects.ToList())
        {
            if (!project.AnalyzerReferences.OfType<AnalyzerFileReference>().Any(r => copies.ContainsKey(r.FullPath)))
            {
                continue;
            }

            solution = solution.WithProjectAnalyzerReferences(project.Id, project.AnalyzerReferences.Select(reference =>
                reference is AnalyzerFileReference file && copies.TryGetValue(file.FullPath, out var copy)
                    ? new AnalyzerFileReference(copy, loader)
                    : reference));
        }

        return (solution, issues);
    }

    // Two loads in one process may share analyzers (a package's, an SDK's): the copy appears whole, once.
    private static void CopyOnce(FileInfo file, string copy)
    {
        var partial = $"{copy}.{Guid.NewGuid():N}.partial";
        file.CopyTo(partial);
        try
        {
            File.Move(partial, copy);
        }
        catch (IOException) when (File.Exists(copy))
        {
            File.Delete(partial);
        }
    }

    private static string FolderName(string folder, IEnumerable<FileInfo> files)
    {
        var key = new StringBuilder(folder.ToUpperInvariant());
        foreach (var file in files)
        {
            key.Append('|').Append(file.Name.ToUpperInvariant()).Append('|').Append(file.Length).Append('|').Append(file.LastWriteTimeUtc.Ticks);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToString())))[..16];
    }

    private static string CreateDirectory()
    {
        // In the background: the folders of earlier servers can hold tens of megabytes, and this runs inside a load.
        _ = Task.Run(() => RemoveLeftovers(Root));
        return Directory.CreateDirectory(Path.Combine(Root, $"{Environment.ProcessId}-{Guid.NewGuid():N}")).FullName;
    }

    // The copies a server loaded stay locked until its process exits: remove those of servers that are gone.
    internal static void RemoveLeftovers(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(directory);
            if (!int.TryParse(name.AsSpan(0, Math.Max(name.IndexOf('-', StringComparison.Ordinal), 0)), out var processId) || IsRunning(processId))
            {
                continue;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: another server may be removing it at the same time.
            }
        }
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            // Another user's process: it may be a server still using its copies.
            return true;
        }
    }
}
