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
        "A .sln, .slnx, .slnf, .csproj or .vbproj, or a directory with one; omit for the solution discovered in the root, else the one in use.";

    public const string MaxTokensDescription =
        "Response budget in tokens (default 1500, max 8000); a cut list says how much it left out.";

    public const string OffsetDescription =
        "Skip this many results, as a cut list suggests.";
}
