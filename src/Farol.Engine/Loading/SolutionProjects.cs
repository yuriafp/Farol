using System.Text.Json;
using Farol.Engine.Workspaces;
using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;

namespace Farol.Engine.Loading;

/// <summary>The project files a workspace target names, read without MSBuild: a .sln or .slnx, a .slnf's subset, or the project itself.</summary>
internal static class SolutionProjects
{
    private static readonly string[] ProjectExtensions = [".csproj", ".vbproj"];

    /// <exception cref="InvalidOperationException">The solution or filter file cannot be read.</exception>
    public static async Task<IReadOnlyList<string>> ListAsync(WorkspaceTarget target, CancellationToken cancellationToken)
    {
        IEnumerable<string> projects;
        try
        {
            projects = target.Kind switch
            {
                WorkspaceTargetKind.Project => [target.Path],
                WorkspaceTargetKind.SolutionFilter => await FilterAsync(target.Path, cancellationToken),
                _ => await SolutionAsync(target.Path, cancellationToken),
            };
        }
        catch (Exception ex) when (ex is SolutionException or KeyNotFoundException or JsonException or ArgumentException)
        {
            throw new InvalidOperationException($"{Path.GetFileName(target.Path)} could not be read: {ex.Message}", ex);
        }

        return [.. projects
            .Where(p => ProjectExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static async Task<IEnumerable<string>> SolutionAsync(string path, CancellationToken cancellationToken)
    {
        var serializer = SolutionSerializers.GetSerializerByMoniker(path)
            ?? throw new InvalidOperationException($"{Path.GetFileName(path)} is not a solution file Farol can read.");
        var model = await serializer.OpenAsync(path, cancellationToken);
        var directory = Path.GetDirectoryName(path)!;
        return model.SolutionProjects.Select(p => Full(directory, p.FilePath));
    }

    // { "solution": { "path": "..\\All.sln", "projects": [ "src\\A\\A.csproj" ] } }: projects are relative to the solution.
    private static async Task<IEnumerable<string>> FilterAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var solution = json.RootElement.GetProperty("solution");
        var solutionPath = Full(Path.GetDirectoryName(path)!, solution.GetProperty("path").GetString() ?? string.Empty);
        var directory = Path.GetDirectoryName(solutionPath)!;
        return solution.TryGetProperty("projects", out var projects)
            ? [.. projects.EnumerateArray().Select(p => p.GetString()).OfType<string>().Select(p => Full(directory, p))]
            : await SolutionAsync(solutionPath, cancellationToken);
    }

    private static string Full(string directory, string path) =>
        Path.GetFullPath(Path.Combine(directory, path.Replace('\\', Path.DirectorySeparatorChar)));
}
