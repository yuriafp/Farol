using System.Globalization;
using System.Text;
using Farol.Engine.Workspaces;
using Farol.Testing;
using Microsoft.CodeAnalysis;

namespace Farol.Engine.Tests;

/// <summary>
/// Records load fidelity for spikes: compiler errors per project after a design-time load. A project
/// that builds with msbuild but shows errors here means the loader missed inputs (e.g. generated code).
/// </summary>
internal static class SpikeReport
{
    public static async Task WriteAsync(string name, Solution solution, LoadReport report, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var invariant = CultureInfo.InvariantCulture;
        text.AppendLine(invariant, $"# {name}");
        text.AppendLine(invariant, $"loader: {report.Loader} · elapsed: {report.Elapsed.TotalSeconds:0.0}s · projects: {report.ProjectFiles} · documents: {report.Documents} · load issues: {report.Issues.Count}");
        foreach (var issue in report.Issues)
        {
            text.AppendLine(invariant, $"- load {issue.Severity}: {issue.Message}");
        }

        text.AppendLine();
        text.AppendLine("| project | language | documents | compiler errors | top error ids |");
        text.AppendLine("|---|---|---|---|---|");
        foreach (var project in solution.Projects.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            var errors = compilation!.GetDiagnostics(cancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            var top = string.Join(", ", errors.GroupBy(d => d.Id).OrderByDescending(g => g.Count()).Take(4).Select(g => $"{g.Key}×{g.Count()}"));
            text.AppendLine(invariant, $"| {project.Name} | {project.Language} | {project.DocumentIds.Count} | {errors.Count} | {top} |");
            foreach (var error in errors.Take(3))
            {
                text.AppendLine(invariant, $"|  | | | | `{error.GetMessage(invariant)}` |");
            }
        }

        Directory.CreateDirectory(TestPaths.SpikeReports);
        await File.WriteAllTextAsync(Path.Combine(TestPaths.SpikeReports, name + ".md"), text.ToString(), cancellationToken);
    }
}
