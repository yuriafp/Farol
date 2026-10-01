using Farol.Core.Execution;

namespace Farol.Testing;

/// <summary>SDK-style fixtures need <c>dotnet restore</c> before MSBuildWorkspace can resolve their references.</summary>
internal static class FixtureRestore
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HashSet<string> Restored = new(StringComparer.OrdinalIgnoreCase);

    public static async Task EnsureRestoredAsync(string solutionPath, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (Restored.Contains(solutionPath))
            {
                return;
            }

            var result = await new LocalProcessRunner().RunAsync(
                new ProcessSpec("dotnet", ["restore", solutionPath], Path.GetDirectoryName(solutionPath)!, TimeSpan.FromMinutes(5)),
                cancellationToken);

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"dotnet restore failed for {solutionPath}:\n{result.StandardOutput}\n{result.StandardError}");
            }

            Restored.Add(solutionPath);
        }
        finally
        {
            Gate.Release();
        }
    }
}
