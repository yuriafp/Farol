using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging.StructuredLogger;

namespace Farol.Engine.Building;

/// <summary>Issues and outputs of a build, collected before deduplication.</summary>
internal sealed class BuildLog
{
    private readonly Dictionary<(bool IsError, string? Code, string Message, string? File, int Line, int Column, string Project), SortedSet<string>> _issues = [];

    public Dictionary<string, List<string>> Outputs { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Add(bool isError, string? code, string? message, string? file, int line, int column, string? projectFile, string? targetFramework)
    {
        // Restore runs in the solution's context but reports issues against a project file (NU1903 on Legacy.Tests.csproj):
        // group them under that project.
        if (IsProjectFile(file))
        {
            projectFile = file;
        }

        var project = string.IsNullOrEmpty(projectFile) ? "(solution)" : Path.GetFileNameWithoutExtension(projectFile);
        var key = (isError, string.IsNullOrEmpty(code) ? null : code, (message ?? string.Empty).Trim(), string.IsNullOrEmpty(file) ? null : file, line, column, project);
        if (!_issues.TryGetValue(key, out var frameworks))
        {
            frameworks = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            _issues[key] = frameworks;
        }

        if (!string.IsNullOrEmpty(targetFramework))
        {
            frameworks.Add(targetFramework);
        }
    }

    private static bool IsProjectFile(string? file) =>
        file is not null && (file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase));

    public void AddOutput(string project, string path)
    {
        if (!Outputs.TryGetValue(project, out var paths))
        {
            paths = [];
            Outputs[project] = paths;
        }

        if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            paths.Add(path);
        }
    }

    public (List<BuildIssue> Errors, List<BuildIssue> Warnings) Issues()
    {
        var issues = _issues
            .Select(kv => new BuildIssue(kv.Key.IsError, kv.Key.Code, kv.Key.Message, kv.Key.File, kv.Key.Line, kv.Key.Column, kv.Key.Project, [.. kv.Value]))
            .OrderBy(i => i.Project, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Line)
            .ThenBy(i => i.Column)
            .ToList();
        return ([.. issues.Where(i => i.IsError)], [.. issues.Where(i => !i.IsError)]);
    }
}

/// <summary>
/// Reads errors, warnings and outputs from an MSBuild binary log: structured, so every project and target framework
/// is known, and the same compiler error reported by two target-framework builds collapses into one issue.
/// </summary>
internal static partial class BuildLogReader
{
    public static BuildLog? ReadBinaryLog(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var log = new BuildLog();
        var frameworks = new Dictionary<int, string?>();
        var reader = new BinLogReader();
        reader.ProjectStarted += (_, e) =>
        {
            if (e.BuildEventContext is { } context)
            {
                frameworks[context.ProjectContextId] = e.GlobalProperties is { } properties && properties.TryGetValue("TargetFramework", out var tfm) ? tfm : null;
            }
        };
        reader.ErrorRaised += (_, e) => log.Add(true, e.Code, e.Message, e.File, e.LineNumber, e.ColumnNumber, e.ProjectFile, FrameworkOf(e.BuildEventContext));
        reader.WarningRaised += (_, e) => log.Add(false, e.Code, e.Message, e.File, e.LineNumber, e.ColumnNumber, e.ProjectFile, FrameworkOf(e.BuildEventContext));
        reader.MessageRaised += (_, e) =>
        {
            if (e.Importance == MessageImportance.High && e.Message is { } message && Output().Match(message) is { Success: true } match)
            {
                log.AddOutput(match.Groups["project"].Value, match.Groups["path"].Value.Trim());
            }
        };

        try
        {
            reader.Replay(path);
            return log;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or EndOfStreamException or FormatException)
        {
            // A binary log newer than this reader understands: the caller falls back to the console output.
            return null;
        }

        string? FrameworkOf(BuildEventContext? context) =>
            context is not null && frameworks.TryGetValue(context.ProjectContextId, out var framework) ? framework : null;
    }

    /// <summary>Fallback: MSBuild's canonical console format, "file(line,col): error CODE: message [project::TargetFramework=tfm]".</summary>
    public static BuildLog ReadConsoleOutput(string output)
    {
        var log = new BuildLog();
        foreach (var line in output.Split('\n'))
        {
            if (Canonical().Match(line.TrimEnd('\r')) is { Success: true } match)
            {
                log.Add(
                    match.Groups["severity"].Value == "error",
                    match.Groups["code"].Value,
                    match.Groups["message"].Value,
                    match.Groups["file"].Success ? match.Groups["file"].Value.Trim() : null,
                    match.Groups["line"].Success ? int.Parse(match.Groups["line"].ValueSpan, System.Globalization.CultureInfo.InvariantCulture) : 0,
                    match.Groups["column"].Success ? int.Parse(match.Groups["column"].ValueSpan, System.Globalization.CultureInfo.InvariantCulture) : 0,
                    match.Groups["project"].Success ? match.Groups["project"].Value : null,
                    match.Groups["tfm"].Success ? match.Groups["tfm"].Value : null);
            }
            else if (Output().Match(line.Trim()) is { Success: true } built && File.Exists(built.Groups["path"].Value.Trim()))
            {
                log.AddOutput(built.Groups["project"].Value, built.Groups["path"].Value.Trim());
            }
        }

        return log;
    }

    // "Legacy.Core -> C:\repo\Legacy.Core\bin\Debug\Legacy.Core.dll"
    [GeneratedRegex(@"^\s*(?<project>[^\s>]+) -> (?<path>.+)$")]
    private static partial Regex Output();

    [GeneratedRegex(@"^\s*(?:(?<file>[^\s(][^(]*?)(?:\((?<line>\d+)(?:,(?<column>\d+))?(?:,\d+,\d+)?\))?\s*:\s*)?(?<severity>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<message>.*?)(?:\s+\[(?<project>[^\]]+?)(?:::TargetFramework=(?<tfm>[^\]]+))?\])?\s*$")]
    private static partial Regex Canonical();
}
