using System.Text;

namespace Farol.Core.Files;

/// <summary>
/// Writes through a temporary file in the same directory, then renames it over the target: compilers, editors and
/// file watchers see the old content or the new one, never a partial file.
/// </summary>
public static class AtomicFile
{
    public static async Task WriteAllTextAsync(string path, string content, Encoding encoding, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(encoding);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, content, encoding, cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The original failure matters more than a leftover temporary file.
        }
    }
}
