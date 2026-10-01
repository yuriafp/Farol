namespace Farol.Testing;

internal static class TestPaths
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string Fixtures => Path.Combine(RepoRoot, "tests", "fixtures");

    public static string ModernDirectory => Path.Combine(Fixtures, "modern");

    public static string ModernSolution => Path.Combine(ModernDirectory, "Modern.slnx");

    public static string LegacyDirectory => Path.Combine(Fixtures, "legacy");

    public static string LegacySolution => Path.Combine(LegacyDirectory, "Legacy.sln");

    public static string SpikeReports => Path.Combine(RepoRoot, "artifacts", "spikes");

    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Farol.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not find Farol.slnx above the test output directory.");
    }
}
