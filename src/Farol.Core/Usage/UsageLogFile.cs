using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Farol.Core.Usage;

/// <summary>
/// The usage log on disk: one JSON line per event in <c>usage-{date}-{process id}.jsonl</c>, so concurrent Farol
/// processes never share a file. Opening it deletes the files older than <see cref="RetentionDays"/> days.
/// </summary>
public sealed class UsageLogFile : IUsageLog
{
    public const int RetentionDays = 30;

    public const string FilePattern = "usage-*.jsonl";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _session = Guid.NewGuid().ToString("N")[..8];
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private bool _broken;

    /// <summary>Creates the directory if needed; throws when it cannot be used, so the host can run without the log.</summary>
    public UsageLogFile(string directory, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        LogDirectory = Path.GetFullPath(directory);
        _time = time ?? TimeProvider.System;
        Directory.CreateDirectory(LogDirectory);
        DeleteExpired();
    }

    /// <summary>Where the usage log goes unless configured otherwise: Farol/usage in the user's local application data.</summary>
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Farol", "usage");

    public string LogDirectory { get; }

    public bool Enabled => true;

    public void Write(UsageEvent usageEvent)
    {
        ArgumentNullException.ThrowIfNull(usageEvent);
        var now = _time.GetLocalNow();
        var line = JsonSerializer.Serialize(usageEvent with { Timestamp = now, Session = _session }, UsageEvent.Json) + "\n";
        var path = Path.Combine(LogDirectory, string.Create(CultureInfo.InvariantCulture, $"usage-{now:yyyy-MM-dd}-{Environment.ProcessId}.jsonl"));
        lock (_gate)
        {
            if (_broken)
            {
                return;
            }

            try
            {
                File.AppendAllText(path, line, Utf8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The log is evidence about Farol, not a feature of it: losing it must never fail a tool call.
                _broken = true;
            }
        }
    }

    private void DeleteExpired()
    {
        var cutoff = _time.GetUtcNow().UtcDateTime.AddDays(-RetentionDays);
        foreach (var file in new DirectoryInfo(LogDirectory).EnumerateFiles(FilePattern))
        {
            try
            {
                if (file.LastWriteTimeUtc < cutoff)
                {
                    file.Delete();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another process may still write it; the next start tries again.
            }
        }
    }
}
