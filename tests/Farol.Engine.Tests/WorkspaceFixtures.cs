using Farol.Engine.Workspaces;
using Farol.Testing;
using Xunit;

namespace Farol.Engine.Tests;

/// <summary>Tests that load tests/fixtures/legacy share one load and never run concurrently (design-time builds write obj/).</summary>
[CollectionDefinition(Name)]
public sealed class LegacyFixtureDefinition : ICollectionFixture<LegacyWorkspaceFixture>
{
    public const string Name = "legacy fixture";
}

/// <summary>Tests that load tests/fixtures/modern share one restore and load.</summary>
[CollectionDefinition(Name)]
public sealed class ModernFixtureDefinition : ICollectionFixture<ModernWorkspaceFixture>
{
    public const string Name = "modern fixture";
}

public sealed class LegacyWorkspaceFixture : IAsyncLifetime
{
    private EngineHarness? _engine;

    public WorkspaceSnapshot? Snapshot { get; private set; }

    public string Root { get; } = TestPaths.LegacyDirectory;

    /// <summary>Classic projects need Windows with Visual Studio or Build Tools; tests skip without them.</summary>
    public bool Available => Snapshot is not null;

    public async ValueTask InitializeAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _engine = EngineHarness.Create(Root);
        var toolchain = await _engine.Toolchain.ProbeAsync(Root, CancellationToken.None);
        if (toolchain.PreferredVisualStudio is not null)
        {
            Snapshot = await _engine.Workspaces.GetSession(null).GetSnapshotAsync(wait: true, CancellationToken.None);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_engine is not null)
        {
            await _engine.DisposeAsync();
        }
    }
}

public sealed class ModernWorkspaceFixture : IAsyncLifetime
{
    private EngineHarness? _engine;

    public WorkspaceSnapshot Snapshot { get; private set; } = null!;

    public WorkspaceSession Session { get; private set; } = null!;

    public string Root { get; } = TestPaths.ModernDirectory;

    public async ValueTask InitializeAsync()
    {
        await FixtureRestore.EnsureRestoredAsync(TestPaths.ModernSolution, CancellationToken.None);
        _engine = EngineHarness.Create(Root);
        Session = _engine.Workspaces.GetSession(null);
        Snapshot = await Session.GetSnapshotAsync(wait: true, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (_engine is not null)
        {
            await _engine.DisposeAsync();
        }
    }
}
