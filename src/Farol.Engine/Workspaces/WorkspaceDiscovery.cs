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

    private static readonly EnumerationOptions Listing = new() { IgnoreInaccessible = true, AttributesToSkip = 0, MatchType = MatchType.Win32 };

    public static WorkspaceTarget Resolve(string? requested, string rootDirectory)
    {
        var root = Path.GetFullPath(rootDirectory);
        var candidate = string.IsNullOrWhiteSpace(requested)
            ? root
            : Path.GetFullPath(Path.IsPathRooted(requested) ? requested : Path.Combine(root, requested));

        if (File.Exists(candidate))
        {
            return FromFile(candidate);
        }

        if (Directory.Exists(candidate))
        {
            return FromDirectory(candidate, root);
        }

        var from = Path.IsPathRooted(requested) ? string.Empty : "; a relative path starts at the root, as responses write paths";
        throw new FarolException(
            ErrorCodes.WorkspaceNotFound,
            $"Workspace '{requested}' was not found.",
            $"Pass a .sln, .slnx, .slnf, .csproj or .vbproj file, or a directory that contains one{from}.");
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

    // Paths in messages are relative to the root, as everywhere else (AC-30): the candidates can be passed back as they are.
    private static WorkspaceTarget FromDirectory(string directory, string root)
    {
        var solutions = FindFiles(directory, ["*.slnx", "*.sln"], depth: 0);
        if (solutions.Count > 0)
        {
            return PickSingle(solutions, root, WorkspaceTargetKind.Solution);
        }

        var projects = FindFiles(directory, ["*.csproj", "*.vbproj"], depth: 0);
        if (projects.Count > 0)
        {
            return PickSingle(projects, root, WorkspaceTargetKind.Project);
        }

        var nested = FindFiles(directory, ["*.slnx", "*.sln"], NestedSearchDepth);
        if (nested.Count > 0)
        {
            return PickSingle(nested, root, WorkspaceTargetKind.Solution);
        }

        var where = string.Equals(directory, root, StringComparison.OrdinalIgnoreCase) ? "the root" : $"'{DisplayPath.From(root, directory)}'";
        throw new FarolException(
            ErrorCodes.WorkspaceNotFound,
            $"No .sln, .slnx, .csproj or .vbproj found in {where}.",
            "Pass the workspace path explicitly; later calls can then leave it out.");
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
            "Pass one of them in the 'workspace' parameter (later calls can then leave it out), or start the server with --workspace.");
    }

    // A root such as a drive or a user profile holds folders nobody may list; they hold no workspace either.
    private static List<string> FindFiles(string directory, string[] patterns, int depth)
    {
        var found = patterns.SelectMany(p => Directory.EnumerateFiles(directory, p, Listing)).ToList();
        if (depth > 0)
        {
            foreach (var child in Directory.EnumerateDirectories(directory, "*", Listing))
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
