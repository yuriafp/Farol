namespace Farol.Tools.Infrastructure;

/// <summary>Parameter conventions shared by every tool, so agents learn them once.</summary>
internal static class ToolParameters
{
    /// <summary>
    /// Mirrored into the <c>Mcp-Param-Workspace</c> HTTP header, letting a gateway route calls to the
    /// worker that holds the workspace without parsing the request body.
    /// </summary>
    public const string WorkspaceHeader = "Workspace";

    public const string WorkspaceDescription =
        "Path to a .sln, .slnx, .slnf, .csproj or .vbproj file, or a directory containing one. Omit to use the solution discovered in the server's working directory.";

    public const string MaxTokensDescription =
        "Response budget in tokens (default 1500, max 8000). Lists are cut to fit and say how many items were left out.";

    public const string OffsetDescription =
        "Skip this many results; use the offset suggested at the end of a cut list to continue it.";
}
