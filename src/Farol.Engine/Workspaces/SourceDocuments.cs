using System.Collections.Immutable;
using Farol.Core;
using Microsoft.CodeAnalysis;

namespace Farol.Engine.Workspaces;

/// <summary>The documents of a source file a tool was given: one per target framework of the project that compiles it.</summary>
public static class SourceDocuments
{
    /// <summary>
    /// The file's documents, or an error that names the file as the caller wrote it and says why there are none: there
    /// is no such file, it is a folder or not C#/VB, or no project of the workspace compiles it.
    /// </summary>
    public static ImmutableArray<DocumentId> Find(WorkspaceSnapshot snapshot, string fullPath, string given)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var ids = snapshot.Solution.GetDocumentIdsWithFilePath(fullPath);
        if (!ids.IsEmpty)
        {
            return ids;
        }

        throw new FarolException(ErrorCodes.InvalidArgument, $"'{given}' is not a source file of the workspace.", Hint(snapshot, fullPath));
    }

    // Like every response, hints write no absolute path: the caller already knows the path it gave.
    private static string Hint(WorkspaceSnapshot snapshot, string fullPath)
    {
        if (Directory.Exists(fullPath))
        {
            return "It is a folder: pass one of its .cs or .vb files.";
        }

        if (!File.Exists(fullPath))
        {
            return "No file has that path, relative to the root Farol runs in or to the workspace's folder: pass it as other dotnet_* tools write it.";
        }

        var extension = Path.GetExtension(fullPath);
        if (!extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".vb", StringComparison.OrdinalIgnoreCase))
        {
            return "Only C# and VB files (.cs, .vb) are source files.";
        }

        var containing = snapshot.Projects.ProjectsContaining(fullPath);
        return snapshot.Projects.Get(containing.Count > 0 ? containing[0] : null) switch
        {
            null => "It is not inside the folder of any project of the workspace.",
            { IsSdkStyle: false } project =>
                $"{Path.GetFileName(project.FilePath)} is a classic project, which compiles only the files it lists: add this one to it (Farol reloads when the project file changes).",
            var project =>
                $"{Path.GetFileName(project.FilePath)} leaves it out (bin, obj, Compile Remove); a file created a moment ago is picked up within seconds.",
        };
    }
}
