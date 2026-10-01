using System.Diagnostics;
using System.Globalization;
using System.Text;
using Basic.CompilerLog.Util;
using Farol.Core.Execution;
using Farol.Testing;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Farol.Engine.Tests;

/// <summary>
/// Spike A, second path: build once with -bl and rebuild the exact compilations from the binary log
/// (Basic.CompilerLog). Compared with the design-time load in <see cref="LegacyWorkspaceTests"/>.
/// </summary>
/// <remarks>
/// Finding: classic VB projects get mscorlib and Microsoft.VisualBasic implicitly from vbc's /sdkpath
/// (C# projects pass mscorlib as an explicit /reference). The replay does not reproduce that, so raw VB
/// compilations miss the core library. <see cref="AddVisualBasicSdkReferences"/> is the workaround.
/// </remarks>
public sealed class LegacyBinlogSpikeTests
{
    private static readonly string[] SdkPathAssemblies = ["mscorlib.dll", "Microsoft.VisualBasic.dll"];

    [Fact]
    public async Task Rebuilds_classic_compilations_from_a_binary_log()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Classic .NET Framework projects need Windows.");
        var ct = TestContext.Current.CancellationToken;
        await using var engine = EngineHarness.Create(TestPaths.LegacyDirectory);
        var toolchain = await engine.Toolchain.ProbeAsync(TestPaths.LegacyDirectory, ct);
        Assert.SkipWhen(toolchain.PreferredVisualStudio?.MSBuildPath is null, "Visual Studio or Build Tools MSBuild is not installed.");

        // A copy: the rebuild rewrites bin/obj, which would race with other tests loading the shared fixture.
        using var copy = FixtureCopy.Create(TestPaths.LegacyDirectory);
        Directory.CreateDirectory(TestPaths.SpikeReports);
        var binlog = Path.Combine(TestPaths.SpikeReports, "legacy.binlog");
        var build = await new LocalProcessRunner().RunAsync(
            new ProcessSpec(
                toolchain.PreferredVisualStudio!.MSBuildPath!,
                // Rebuild: an up-to-date build skips CoreCompile and the log would hold no compiler calls.
                [copy.PathOf("Legacy.sln"), "-restore", "-t:Rebuild", "-nodeReuse:false", "-v:quiet", "-nologo", $"-bl:{binlog}"],
                copy.Root,
                TimeSpan.FromMinutes(5)),
            ct);
        Assert.True(build.ExitCode == 0, build.StandardOutput + build.StandardError);

        var read = Stopwatch.StartNew();
        using var reader = BinaryLogReader.Create(binlog, null, null);
        var calls = reader.ReadAllCompilerCalls(static _ => true);
        var rows = new List<(string Assembly, string Language, int Trees, int RawErrors, int FixedErrors, string TopRaw)>();
        foreach (var call in calls)
        {
            var raw = reader.ReadCompilationData(call).GetCompilationAfterGenerators(ct);
            var rawErrors = Errors(raw, ct);
            var fixedErrors = raw.Language == LanguageNames.VisualBasic
                ? Errors(AddVisualBasicSdkReferences(raw, reader.ReadArguments(call)), ct)
                : rawErrors;
            var top = string.Join(", ", rawErrors.GroupBy(d => d.Id).OrderByDescending(g => g.Count()).Take(3).Select(g => $"{g.Key}×{g.Count()}"));
            rows.Add((raw.AssemblyName!, raw.Language, raw.SyntaxTrees.Count(), rawErrors.Count, fixedErrors.Count, top));
        }

        read.Stop();

        var text = new StringBuilder();
        var invariant = CultureInfo.InvariantCulture;
        text.AppendLine("# spike-a-binlog");
        text.AppendLine(invariant, $"msbuild rebuild (ground truth): exit {build.ExitCode} in {build.Elapsed.TotalSeconds:0.0}s · replay of {rows.Count} compilations: {read.Elapsed.TotalSeconds:0.00}s");
        text.AppendLine();
        text.AppendLine("| assembly | language | syntax trees | errors (raw replay) | errors (with VB /sdkpath fix) | top raw error ids |");
        text.AppendLine("|---|---|---|---|---|---|");
        foreach (var row in rows.OrderBy(r => r.Assembly, StringComparer.Ordinal))
        {
            text.AppendLine(invariant, $"| {row.Assembly} | {row.Language} | {row.Trees} | {row.RawErrors} | {row.FixedErrors} | {row.TopRaw} |");
        }

        await File.WriteAllTextAsync(Path.Combine(TestPaths.SpikeReports, "spike-a-binlog.md"), text.ToString(), ct);

        Assert.True(rows.Count >= 5, $"Expected a compilation per project, got {rows.Count}.");
        Assert.All(rows.Where(r => r.Language == LanguageNames.CSharp), r => Assert.Equal(0, r.RawErrors));
        Assert.All(rows, r => Assert.Equal(0, r.FixedErrors));
    }

    private static List<Diagnostic> Errors(Compilation compilation, CancellationToken ct) =>
        [.. compilation.GetDiagnostics(ct).Where(d => d.Severity == DiagnosticSeverity.Error)];

    /// <summary>Adds the references vbc resolves implicitly from /sdkpath when they are missing.</summary>
    private static Compilation AddVisualBasicSdkReferences(Compilation compilation, IReadOnlyCollection<string> arguments)
    {
        var sdkPath = arguments
            .Where(a => a.StartsWith("/sdkpath:", StringComparison.OrdinalIgnoreCase))
            .Select(a => a["/sdkpath:".Length..].Trim('"'))
            .LastOrDefault();
        if (sdkPath is null)
        {
            return compilation;
        }

        var present = compilation.References
            .OfType<PortableExecutableReference>()
            .Select(r => Path.GetFileName(r.FilePath))
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = SdkPathAssemblies
            .Where(name => !present.Contains(name))
            .Select(name => Path.Combine(sdkPath, name))
            .Where(File.Exists)
            .Select(path => MetadataReference.CreateFromFile(path));

        return compilation.AddReferences(missing);
    }
}
