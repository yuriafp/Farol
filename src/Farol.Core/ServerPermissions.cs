namespace Farol.Core;

/// <summary>
/// What the server may do beyond reading code. <c>--read-only</c> refuses writing files, building and running
/// tests, so an agent can explore a repository without side effects; the refusal says why and what still works.
/// </summary>
public sealed class ServerPermissions(bool readOnly)
{
    public bool ReadOnly { get; } = readOnly;

    public void Demand(string operation, string alternative)
    {
        if (ReadOnly)
        {
            throw new FarolException(
                ErrorCodes.ReadOnlyMode,
                $"Refused to {operation}: Farol was started with --read-only, which blocks writing files, building and running tests.",
                alternative);
        }
    }
}
