using Farol.Core;
using Farol.Core.Paths;

namespace Farol.Engine.Workspaces;

/// <summary>
/// Resolves what the caller asked for (a file, a directory or nothing) into a single workspace target.
/// Ambiguity is never guessed away: the error lists the candidates so the agent can pick one.
/// </summary>
public static class WorkspaceDiscovery
{
    private const int NestedSearchDepth = 2;

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".idea", "node_modules", "packages", "artifacts", "TestResults",
    };

    public static WorkspaceTarget Resolve(string? requested, string rootDirectory)
    {
        var candidate = string.IsNullOrWhiteSpace(requested)
            ? Path.GetFullPath(rootDirectory)
            : Path.GetFullPath(Path.IsPathRooted(requested) ? requested : Path.Combine(rootDirectory, requested));

        if (File.Exists(candidate))
        {
            return FromFile(candidate);
        }

        if (Directory.Exists(candidate))
        {
            return FromDirectory(candidate);
        }

        throw new FarolException(
            ErrorCodes.WorkspaceNotFound,
            $"Workspace '{requested}' was not found.",
            "Pass a .sln, .slnx, .slnf, .csproj or .vbproj file, or a directory that contains one.");
    }

    private static WorkspaceTarget FromFile(string path) =>
        Path.GetExtension(path).ToUpperInvariant() switch
        {
            ".SLN" or ".SLNX" => new WorkspaceTarget(path, WorkspaceTargetKind.Solution),
            ".SLNF" => new WorkspaceTarget(path, WorkspaceTargetKind.SolutionFilter),
            ".CSPROJ" or ".VBPROJ" => new WorkspaceTarget(path, WorkspaceTargetKind.Project),
            _ => throw new FarolException(
                ErrorCodes.WorkspaceNotFound,
                $"'{Path.GetFileName(path)}' is not a solution or a C#/VB project.",
                "Supported: .sln, .slnx, .slnf, .csproj, .vbproj."),
        };

    private static WorkspaceTarget FromDirectory(string directory)
    {
        var solutions = FindFiles(directory, ["*.slnx", "*.sln"], depth: 0);
        if (solutions.Count > 0)
        {
            return PickSingle(solutions, directory, WorkspaceTargetKind.Solution);
        }

        var projects = FindFiles(directory, ["*.csproj", "*.vbproj"], depth: 0);
        if (projects.Count > 0)
        {
            return PickSingle(projects, directory, WorkspaceTargetKind.Project);
        }

        var nested = FindFiles(directory, ["*.slnx", "*.sln"], NestedSearchDepth);
        if (nested.Count > 0)
        {
            return PickSingle(nested, directory, WorkspaceTargetKind.Solution);
        }

        throw new FarolException(
            ErrorCodes.WorkspaceNotFound,
            $"No .sln, .slnx, .csproj or .vbproj found in '{directory}'.",
            "Pass the workspace path explicitly.");
    }

    private static WorkspaceTarget PickSingle(List<string> candidates, string root, WorkspaceTargetKind kind)
    {
        // The same solution in both formats is common right after migrating to .slnx: prefer .slnx.
        var distinct = candidates
            .GroupBy(p => Path.ChangeExtension(p, null), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(p => p.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ? 0 : 1).First())
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinct.Count == 1)
        {
            return new WorkspaceTarget(distinct[0], kind);
        }

        var list = string.Join(", ", distinct.Select(p => DisplayPath.From(root, p)));
        throw new FarolException(
            ErrorCodes.WorkspaceAmbiguous,
            $"Found {distinct.Count} candidates: {list}.",
            "Pass one of them in the 'workspace' parameter, or start the server with --workspace.");
    }

    private static List<string> FindFiles(string directory, string[] patterns, int depth)
    {
        var found = patterns.SelectMany(p => Directory.EnumerateFiles(directory, p)).ToList();
        if (depth > 0)
        {
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (!SkippedDirectories.Contains(Path.GetFileName(child)))
                {
                    found.AddRange(FindFiles(child, patterns, depth - 1));
                }
            }
        }

        return found;
    }
}
