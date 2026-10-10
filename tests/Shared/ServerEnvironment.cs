namespace Farol.Testing;

/// <summary>
/// The environment of a Farol server a test starts. A developer may keep the usage log of their real use on with
/// <c>Farol__UsageLog</c> (AC-39), and a server inherits it: tests drop it, so they never write to that log, and turn
/// the log on only with their own arguments, into their own directory.
/// </summary>
internal static class ServerEnvironment
{
    /// <summary>For the stdio transport's environment variables, where a null value removes an inherited one.</summary>
    public static Dictionary<string, string?> WithoutUsageLog() =>
        Environment.GetEnvironmentVariables().Keys.Cast<string>()
            .Where(name => name.StartsWith("Farol__UsageLog", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(name => name, _ => (string?)null);
}
