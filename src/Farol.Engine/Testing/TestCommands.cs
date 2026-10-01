using System.Text;
using System.Text.Json;

namespace Farol.Engine.Testing;

/// <summary>How a test project's tests are run.</summary>
public enum TestHost
{
    /// <summary>Classic .NET Framework test projects: Visual Studio's vstest.console.exe on the built assembly.</summary>
    VSTestConsole,

    /// <summary>SDK-style projects in the default mode: <c>dotnet test</c> with the VSTest logger.</summary>
    DotnetTestVSTest,

    /// <summary>SDK-style projects whose global.json opts into Microsoft.Testing.Platform (the .NET 10 <c>dotnet test</c>).</summary>
    DotnetTestPlatform,
}

/// <summary>Which tests of a project to run: every one, those whose name contains a text, or an exact list.</summary>
public sealed record TestTarget(TestProjectInfo Project, IReadOnlyList<string>? TestNames = null, string? NameContains = null);

/// <summary>Command lines for each test host, kept pure so every combination is testable without running anything.</summary>
public static class TestCommands
{
    // Windows caps a command line at 32K characters; leave room for everything else.
    private const int MaxFilterLength = 8000;

    public static TestHost HostFor(TestProjectInfo project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return !project.IsSdkStyle ? TestHost.VSTestConsole
            : UsesTestingPlatform(Path.GetDirectoryName(project.FilePath)!) ? TestHost.DotnetTestPlatform
            : TestHost.DotnetTestVSTest;
    }

    /// <summary>
    /// A VSTest filter expression (also accepted by xUnit v3, MSTest and NUnit on Microsoft.Testing.Platform), or null
    /// to run everything. Exact names first; a list too long for a command line falls back to the classes that hold them.
    /// </summary>
    public static string? Filter(TestTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.TestNames is { } names)
        {
            // NUnit names parameterized cases "Method(1,2)": match those too.
            var nunit = target.Project.Frameworks.Contains(TestFrameworks.NUnit);
            var exact = string.Join('|', names.SelectMany(n => nunit
                ? new[] { $"FullyQualifiedName={Escape(n)}", $"FullyQualifiedName~{Escape(n)}\\(" }
                : [$"FullyQualifiedName={Escape(n)}"]));
            if (exact.Length <= MaxFilterLength)
            {
                return exact;
            }

            var classes = string.Join('|', names.Select(n => n[..n.LastIndexOf('.')]).Distinct(StringComparer.Ordinal).Select(c => $"FullyQualifiedName~{Escape(c)}."));
            return classes.Length <= MaxFilterLength ? classes : null;
        }

        return target.NameContains is { Length: > 0 } text ? $"FullyQualifiedName~{Escape(text)}" : null;
    }

    public static IReadOnlyList<string> Arguments(TestHost host, TestTarget target, string assemblyOrProject, string configuration, string resultsDirectory, string trxFileName)
    {
        ArgumentNullException.ThrowIfNull(target);
        var filter = Filter(target);
        switch (host)
        {
            case TestHost.VSTestConsole:
                return [assemblyOrProject, $"/Logger:trx;LogFileName={trxFileName}", $"/ResultsDirectory:{resultsDirectory}", .. filter is null ? Array.Empty<string>() : [$"/TestCaseFilter:{filter}"]];
            case TestHost.DotnetTestVSTest:
                return ["test", assemblyOrProject, "--no-build", "-c", configuration, "--logger", $"trx;LogFileName={trxFileName}", "--results-directory", resultsDirectory,
                    .. filter is null ? Array.Empty<string>() : ["--filter", filter]];
            default:
                // xUnit v3 writes TRX with its own reporter; MSTest, NUnit and TUnit use the platform's TRX extension.
                string[] trx = target.Project.Frameworks.Contains(TestFrameworks.XUnit)
                    ? ["--report-xunit-trx", "--report-xunit-trx-filename", trxFileName]
                    : ["--report-trx", "--report-trx-filename", trxFileName];
                var supportsFilter = !target.Project.Frameworks.Contains(TestFrameworks.TUnit);
                return ["test", "--project", assemblyOrProject, "--no-build", "-c", configuration, "--results-directory", resultsDirectory, "--no-progress", "--no-ansi", .. trx,
                    .. filter is not null && supportsFilter ? ["--filter", filter] : Array.Empty<string>()];
        }
    }

    /// <summary>True when the nearest global.json sets <c>"test": { "runner": "Microsoft.Testing.Platform" }</c>.</summary>
    public static bool UsesTestingPlatform(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            var globalJson = Path.Combine(current.FullName, "global.json");
            if (!File.Exists(globalJson))
            {
                continue;
            }

            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(globalJson), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                return json.RootElement.TryGetProperty("test", out var test)
                    && test.TryGetProperty("runner", out var runner)
                    && string.Equals(runner.GetString(), "Microsoft.Testing.Platform", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                return false;
            }
        }

        return false;
    }

    // VSTest filter syntax reserves these characters; a backslash makes them literal.
    private static string Escape(string value)
    {
        var escaped = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '\\' or '(' or ')' or '&' or '|' or '=' or '!' or '~')
            {
                escaped.Append('\\');
            }

            escaped.Append(c);
        }

        return escaped.ToString();
    }
}
