using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Farol.Core.Paths;

namespace Farol.Engine.Testing;

/// <summary>One test result from a TRX file.</summary>
public sealed record TestCaseResult(string Name, string Outcome, string? Message, string? StackTrace)
{
    public bool Failed => Outcome is "Failed" or "Error" or "Timeout" or "Aborted";

    public bool Passed => Outcome == "Passed";
}

/// <summary>
/// Reads TRX, the results format VSTest, Microsoft.Testing.Platform and xUnit's own reporter share. Local names only:
/// producers disagree on namespaces.
/// </summary>
public static class TrxReader
{
    public static IReadOnlyList<TestCaseResult>? Read(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return [.. XDocument.Load(path).Descendants().Where(e => e.Name.LocalName == "UnitTestResult").Select(result => new TestCaseResult(
                (string?)result.Attribute("testName") ?? "(unnamed test)",
                (string?)result.Attribute("outcome") ?? "Unknown",
                Child(result, "Message"),
                Child(result, "StackTrace")))];
        }
        catch (Exception ex) when (ex is IOException or XmlException)
        {
            return null;
        }
    }

    private static string? Child(XElement result, string name) =>
        result.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
}

/// <summary>
/// Keeps the stack frames that point into the user's code: framework and test-runner frames have no file in the
/// repository and cost tokens without helping. Tolerates localized .NET Framework traces ("em … na …:linha 12").
/// </summary>
public static partial class StackFrames
{
    public static IReadOnlyList<string> InUserCode(string? stackTrace, string root, Func<string, bool> isUserCode)
    {
        ArgumentNullException.ThrowIfNull(isUserCode);
        if (string.IsNullOrWhiteSpace(stackTrace))
        {
            return [];
        }

        var frames = new List<string>();
        foreach (var line in stackTrace.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Frame().Match(line) is { Success: true } match && match.Groups["path"].Success && isUserCode(match.Groups["path"].Value))
            {
                var number = int.Parse(match.Groups["line"].ValueSpan, System.Globalization.CultureInfo.InvariantCulture);
                frames.Add($"at {match.Groups["method"].Value} · {DisplayPath.Location(root, match.Groups["path"].Value, number)}");
            }
        }

        return frames;
    }

    // "at Ns.Type.Method(Int32 x) in C:\repo\File.cs:line 42" in any language: a word, the method, then "<word> <path>:<word> <line>".
    [GeneratedRegex(@"^\S+\s+(?<method>.+?\))(?:\s+\S+\s+(?<path>(?:[A-Za-z]:[\\/]|/).+?):\S+\s+(?<line>\d+))?\s*$")]
    private static partial Regex Frame();
}
