using System.Security.Cryptography;
using System.Text;

namespace Farol.Core.Paths;

/// <summary>
/// Where Farol keeps what its builds and test runs produce (binary logs, TRX files): under the temp directory,
/// one folder per workspace, never inside the user's repository.
/// </summary>
public static class ArtifactDirectory
{
    public static string For(string kind, string workspacePath)
    {
        var normalized = OperatingSystem.IsWindows() ? workspacePath.ToUpperInvariant() : workspacePath;
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..12];
        var directory = Path.Combine(Path.GetTempPath(), "farol", kind, id);
        Directory.CreateDirectory(directory);
        return directory;
    }
}
