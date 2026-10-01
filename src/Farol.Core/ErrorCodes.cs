namespace Farol.Core;

/// <summary>Stable, machine-readable codes carried by <see cref="FarolException"/>.</summary>
public static class ErrorCodes
{
    public const string Unexpected = "unexpected";
    public const string InvalidArgument = "invalid_argument";
    public const string WorkspaceNotFound = "workspace_not_found";
    public const string WorkspaceAmbiguous = "workspace_ambiguous";
    public const string WorkspaceNotReady = "workspace_not_ready";
    public const string WorkspaceLoadFailed = "workspace_load_failed";
    public const string SymbolNotFound = "symbol_not_found";
    public const string PathNotTrusted = "path_not_trusted";
    public const string ReadOnlyMode = "read_only";
    public const string Conflict = "conflict";
    public const string WriteFailed = "write_failed";
}
