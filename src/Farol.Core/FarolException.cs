namespace Farol.Core;

/// <summary>
/// An expected failure the agent can act on. Hosts surface <see cref="Exception.Message"/> and
/// <see cref="Hint"/> instead of a generic error, so both must say what to do next.
/// </summary>
public sealed class FarolException : Exception
{
    public FarolException(string code, string message, string? hint = null)
        : base(message)
    {
        Code = code;
        Hint = hint;
    }

    public FarolException()
        : this(ErrorCodes.Unexpected, "Unexpected error.")
    {
    }

    public FarolException(string message)
        : this(ErrorCodes.Unexpected, message)
    {
    }

    public FarolException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = ErrorCodes.Unexpected;
    }

    public string Code { get; }

    public string? Hint { get; }
}
