using System.Text.Json;
using Farol.Core.Usage;
using Farol.Testing;
using Xunit;

namespace Farol.Engine.Tests;

/// <summary>AC-39 and AC-40: the usage log that measures real use, and the report that sums it up.</summary>
public sealed class UsageLogTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("farol-usage-");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void AC39_each_event_is_one_json_line_in_a_daily_file_of_the_process()
    {
        var log = new UsageLogFile(_directory.FullName);
        log.Write(new UsageEvent { Kind = UsageEventKind.Call, Tool = "dotnet_check", Outcome = UsageOutcome.Ok, Ms = 120 });
        log.Write(new UsageEvent { Kind = UsageEventKind.Call, Tool = "dotnet_symbol", Outcome = UsageOutcome.Error, Error = "symbol_not_found", Ms = 5 });

        var file = Assert.Single(_directory.GetFiles());
        var lines = File.ReadAllLines(file.FullName);
        using var first = JsonDocument.Parse(lines[0]);
        using var second = JsonDocument.Parse(lines[1]);

        Assert.Matches($@"^usage-\d{{4}}-\d{{2}}-\d{{2}}-{Environment.ProcessId}\.jsonl$", file.Name);
        Assert.Equal(2, lines.Length);
        Assert.Equal((1, "call", 8), (first.RootElement.GetProperty("v").GetInt32(), first.RootElement.GetProperty("event").GetString(), first.RootElement.GetProperty("session").GetString()!.Length));
        Assert.False(first.RootElement.TryGetProperty("error", out _)); // empty fields are left out
        Assert.Equal("symbol_not_found", second.RootElement.GetProperty("error").GetString());
        Assert.Equal(first.RootElement.GetProperty("session").GetString(), second.RootElement.GetProperty("session").GetString());
    }

    [Fact]
    public void AC39_opening_the_log_deletes_its_files_older_than_30_days()
    {
        var old = Touch("usage-2026-01-01-1.jsonl", DateTime.UtcNow.AddDays(-31));
        var recent = Touch("usage-2026-01-02-1.jsonl", DateTime.UtcNow.AddDays(-1));
        var other = Touch("notes.txt", DateTime.UtcNow.AddDays(-90));

        _ = new UsageLogFile(_directory.FullName);

        Assert.Equal((false, true, true), (File.Exists(old), File.Exists(recent), File.Exists(other)));
    }

    [Fact]
    public void AC39_a_solution_appears_only_as_a_hash_of_its_file_name()
    {
        var hash = UsageEvent.HashName("C:/Work/Contoso.Billing.sln");

        Assert.Matches("^[0-9a-f]{8}$", hash);
        Assert.Equal(hash, UsageEvent.HashName("/home/dev/src/CONTOSO.billing.SLN")); // the same solution on another machine
        Assert.NotEqual(hash, UsageEvent.HashName("C:/Work/Contoso.Payroll.sln"));
    }

    [Fact]
    public async Task AC39_a_load_and_its_warm_up_are_recorded_with_the_workspace_as_a_hash()
    {
        using var copy = FixtureCopy.Create(TestPaths.ModernDirectory);
        await FixtureRestore.EnsureRestoredAsync(copy.PathOf("Modern.slnx"), Ct);
        await using (var engine = EngineHarness.Create(copy.Root, new UsageLogFile(_directory.FullName)))
        {
            var session = engine.Workspaces.GetSession(null);
            await session.GetSnapshotAsync(wait: true, Ct);
            await Eventually.MatchesAsync(() => Task.FromResult(session.WarmedIn), warmed => warmed is not null, Ct, timeoutMs: 120_000);
        }

        var events = ReadEvents();
        var load = Assert.Single(events, e => e.Kind == UsageEventKind.Load);
        var warm = Assert.Single(events, e => e.Kind == UsageEventKind.Warm);
        var text = string.Concat(_directory.GetFiles().Select(f => File.ReadAllText(f.FullName)));

        Assert.Equal((UsageEvent.HashName("Modern.slnx"), UsageOutcome.Ok, 0), (load.Workspace, load.Outcome, load.LoadErrors));
        Assert.True(load.Projects > 0 && load.Ms > 0, $"projects {load.Projects}, {load.Ms} ms");
        Assert.Equal((load.Workspace, UsageOutcome.Ok), (warm.Workspace, warm.Outcome));
        Assert.DoesNotContain("modern", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AC40_the_report_sums_up_the_days_clients_tools_errors_and_workspaces()
    {
        var today = new DateOnly(2026, 10, 9);
        var earlier = new UsageLogFile(_directory.FullName, At(today.AddDays(-2)));
        earlier.Write(new UsageEvent { Kind = UsageEventKind.Start, Farol = "0.1.0-alpha.3" });
        earlier.Write(new UsageEvent { Kind = UsageEventKind.Session, Client = "claude-code", ClientVersion = "2.1.286" });
        earlier.Write(new UsageEvent { Kind = UsageEventKind.Load, Workspace = "3fa2b1c9", Outcome = UsageOutcome.Ok, Ms = 41_000, Projects = 81, LoadErrors = 0 });
        earlier.Write(new UsageEvent { Kind = UsageEventKind.Warm, Workspace = "3fa2b1c9", Outcome = UsageOutcome.Ok, Ms = 95_000 });
        earlier.Write(Call("dotnet_find_references", 200));
        earlier.Write(Call("dotnet_find_references", 600));
        earlier.Write(Call("dotnet_symbol", 5) with { Outcome = UsageOutcome.Error, Error = "symbol_not_found" });

        var later = new UsageLogFile(_directory.FullName, At(today));
        later.Write(new UsageEvent { Kind = UsageEventKind.Start, Farol = "0.1.0-alpha.3" });
        later.Write(new UsageEvent { Kind = UsageEventKind.Session, Client = "antigravity", ClientVersion = "1.0" });
        later.Write(Call("dotnet_check", 900) with { Cut = true });
        later.Write(Call("dotnet_build", 40) with { Outcome = UsageOutcome.Error, Error = "unexpected", Exception = "InvalidOperationException" });

        new UsageLogFile(_directory.FullName, At(today.AddDays(-20))).Write(Call("dotnet_outline", 10)); // before the period
        File.AppendAllText(Path.Combine(_directory.FullName, $"usage-{today:yyyy-MM-dd}-1.jsonl"), "{ not json\n");

        var report = UsageReport.Build(_directory.FullName, days: 14, At(today).GetLocalNow());

        Assert.Contains("Farol usage report: 2026-09-26 to 2026-10-09 (14 days)", report, StringComparison.Ordinal);
        Assert.Contains("days with use: 2 (10-07, 10-09)", report, StringComparison.Ordinal);
        Assert.Contains("sessions: 2, Farol 0.1.0-alpha.3 (2)", report, StringComparison.Ordinal);
        Assert.Contains("clients: antigravity 1.0 (1), claude-code 2.1.286 (1)", report, StringComparison.Ordinal);
        Assert.Contains("tool calls: 5, errors: 2 (40%), cancelled: 0, cut by the token budget: 1", report, StringComparison.Ordinal);
        Assert.Matches(@"dotnet_find_references\s+2\s+0\s+200 ms\s+600 ms", report);
        Assert.Contains("errors by code: symbol_not_found (1), unexpected (1: InvalidOperationException (1))", report, StringComparison.Ordinal);
        Assert.Contains("- 3fa2b1c9: 1 load(s), 0 failed, 81 project(s), load p50 41.0 s, slowest 41.0 s, load errors 0, warm-up p50 95.0 s", report, StringComparison.Ordinal);
        Assert.Contains("crashes: none", report, StringComparison.Ordinal);
        Assert.Contains("unreadable lines skipped: 1", report, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet_outline", report, StringComparison.Ordinal);
    }

    [Fact]
    public void AC40_an_empty_log_says_how_to_turn_it_on()
    {
        var report = UsageReport.Build(Path.Combine(_directory.FullName, "missing"), days: 14, DateTimeOffset.Now);

        Assert.Contains("No usage recorded in this period", report, StringComparison.Ordinal);
        Assert.Contains("Farol__UsageLog=true", report, StringComparison.Ordinal);
    }

    public void Dispose() => _directory.Delete(recursive: true);

    private static UsageEvent Call(string tool, long ms) => new() { Kind = UsageEventKind.Call, Tool = tool, Outcome = UsageOutcome.Ok, Ms = ms };

    /// <summary>A clock stopped at noon, local time, on the given day.</summary>
    private static FixedTime At(DateOnly day)
    {
        var noon = day.ToDateTime(new TimeOnly(12, 0));
        return new FixedTime(new DateTimeOffset(noon, TimeZoneInfo.Local.GetUtcOffset(noon)));
    }

    private string Touch(string name, DateTime lastWriteUtc)
    {
        var path = Path.Combine(_directory.FullName, name);
        File.WriteAllText(path, "{}\n");
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    private List<UsageEvent> ReadEvents() =>
        [.. _directory.GetFiles(UsageLogFile.FilePattern).SelectMany(f => File.ReadAllLines(f.FullName)).Select(l => JsonSerializer.Deserialize<UsageEvent>(l, Json)!)];

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }
}
