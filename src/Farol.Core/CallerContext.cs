namespace Farol.Core;

/// <summary>
/// Who is calling. Local stdio sessions use the OS user; a future HTTP host fills this from token
/// claims, which is what makes audit and per-repository authorization pluggable later.
/// </summary>
public sealed record CallerContext(string User, string Origin)
{
    public static CallerContext LocalProcess { get; } = new(Environment.UserName, "stdio");
}
