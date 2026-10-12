using Penghou.Hufu.Sqlite;
using Xunit;

namespace Penghou.Hufu.Tests;

/// <summary>
/// Durable execution-lifecycle state: atomic apply, sequence compare-and-set,
/// transition-identity replay, ordering, and reopen/recovery of state and
/// receipts on a real file-backed SQLite store.
/// </summary>
public sealed class SqliteExecutionLifecycleStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly ExecutionLifecycleBinding Binding = new("tenant", "delegation", "run-child");

    [Fact]
    public async Task ApplySuspend_PersistsStateAndReceipt()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteExecutionLifecycleStore(database.DatabasePath, new Clock(Now));

        var result = await store.ApplyAsync(Transition("t1", 0, ExecutionLifecycleTransitionKind.Suspend));

        Assert.Equal(ExecutionLifecycleApplyStatus.Applied, result.Status);
        Assert.Equal(1, result.Receipt!.Sequence);
        Assert.Equal(ExecutionLifecycleState.Suspended, result.Receipt.State);
        Assert.Equal(Now, result.Receipt.RecordedAt);
        Assert.Equal(ExecutionLifecycleState.Suspended, await store.GetStateAsync(Binding));
    }

    [Fact]
    public async Task Reopen_RecoversStateAndReplaysReceipt()
    {
        using var database = new TemporaryDatabase();
        var first = new SqliteExecutionLifecycleStore(database.DatabasePath, new Clock(Now));
        var applied = await first.ApplyAsync(Transition("t1", 0, ExecutionLifecycleTransitionKind.Suspend));

        var reopened = new SqliteExecutionLifecycleStore(database.DatabasePath, new Clock(Now.AddMinutes(5)));
        Assert.Equal(ExecutionLifecycleState.Suspended, await reopened.GetStateAsync(Binding));

        var replay = await reopened.ApplyAsync(Transition("t1", 0, ExecutionLifecycleTransitionKind.Suspend));
        Assert.Equal(ExecutionLifecycleApplyStatus.Replayed, replay.Status);
        Assert.Equal(applied.Receipt, replay.Receipt);

        var resume = await reopened.ApplyAsync(Transition("t2", 1, ExecutionLifecycleTransitionKind.Resume));
        Assert.Equal(ExecutionLifecycleApplyStatus.Applied, resume.Status);
        Assert.Equal(ExecutionLifecycleState.Active, resume.Receipt!.State);
    }

    [Fact]
    public async Task SameTransitionIdDifferentIntent_Conflicts()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteExecutionLifecycleStore(database.DatabasePath, new Clock(Now));
        await store.ApplyAsync(Transition("t1", 0, ExecutionLifecycleTransitionKind.Suspend));
        var conflict = await store.ApplyAsync(Transition("t1", 0, ExecutionLifecycleTransitionKind.Terminate,
            ExecutionLifecycleTerminalReason.Cancelled));
        Assert.Equal(ExecutionLifecycleApplyStatus.Conflict, conflict.Status);
    }

    [Fact]
    public async Task StaleExpectedSequence_IsRejected()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteExecutionLifecycleStore(database.DatabasePath, new Clock(Now));
        await store.ApplyAsync(Transition("t1", 0, ExecutionLifecycleTransitionKind.Suspend));
        var stale = await store.ApplyAsync(Transition("t2", 0, ExecutionLifecycleTransitionKind.Resume));
        Assert.Equal(ExecutionLifecycleApplyStatus.Stale, stale.Status);
        Assert.Equal(ExecutionLifecycleState.Suspended, await store.GetStateAsync(Binding));
    }

    [Fact]
    public async Task InvalidTransition_Conflicts()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteExecutionLifecycleStore(database.DatabasePath, new Clock(Now));
        var resume = await store.ApplyAsync(Transition("t1", 0, ExecutionLifecycleTransitionKind.Resume));
        Assert.Equal(ExecutionLifecycleApplyStatus.Conflict, resume.Status);
        await store.ApplyAsync(Transition("t2", 0, ExecutionLifecycleTransitionKind.Suspend));
        var suspend = await store.ApplyAsync(Transition("t3", 1, ExecutionLifecycleTransitionKind.Suspend));
        Assert.Equal(ExecutionLifecycleApplyStatus.Conflict, suspend.Status);
    }

    [Fact]
    public async Task TerminateIsTerminal_AcrossReopen()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteExecutionLifecycleStore(database.DatabasePath, new Clock(Now));
        var terminated = await store.ApplyAsync(Transition("t1", 0, ExecutionLifecycleTransitionKind.Terminate,
            ExecutionLifecycleTerminalReason.Completed));
        Assert.Equal(ExecutionLifecycleApplyStatus.Applied, terminated.Status);
        Assert.Equal(ExecutionLifecycleState.Terminal, terminated.Receipt!.State);

        var reopened = new SqliteExecutionLifecycleStore(database.DatabasePath, new Clock(Now.AddMinutes(1)));
        Assert.Equal(ExecutionLifecycleState.Terminal, await reopened.GetStateAsync(Binding));
        var again = await reopened.ApplyAsync(Transition("t2", 1, ExecutionLifecycleTransitionKind.Terminate,
            ExecutionLifecycleTerminalReason.Superseded));
        Assert.Equal(ExecutionLifecycleApplyStatus.Terminated, again.Status);
    }

    [Fact]
    public async Task IndependentBindings_DoNotInterfere()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteExecutionLifecycleStore(database.DatabasePath, new Clock(Now));
        var other = new ExecutionLifecycleBinding("tenant", "delegation", "run-other");
        await store.ApplyAsync(Transition("t1", 0, ExecutionLifecycleTransitionKind.Suspend));
        Assert.Equal(ExecutionLifecycleState.Suspended, await store.GetStateAsync(Binding));
        Assert.Null(await store.GetStateAsync(other));

        var suspendedOther = new ExecutionLifecycleTransition(other, 0, "t-other",
            ExecutionLifecycleTransitionKind.Suspend, ExecutionLifecycleTerminalReason.None, "gen-1", "fence");
        Assert.Equal(ExecutionLifecycleApplyStatus.Applied, (await store.ApplyAsync(suspendedOther)).Status);
        Assert.Equal(ExecutionLifecycleState.Suspended, await store.GetStateAsync(other));
    }

    [Fact]
    public async Task UnknownDatabaseNamespace_IsRejected()
    {
        using var database = new TemporaryDatabase();
        Directory.CreateDirectory(database.DirectoryPath);
        await File.WriteAllTextAsync(database.DatabasePath, "not a lifecycle database");
        var store = new SqliteExecutionLifecycleStore(database.DatabasePath, new Clock(Now));
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await store.GetStateAsync(Binding));
        Assert.False(await store.ApplyAsync(Transition("t1", 0, ExecutionLifecycleTransitionKind.Suspend)) is
            { Status: ExecutionLifecycleApplyStatus.Applied });
    }

    private static ExecutionLifecycleTransition Transition(string id, long expected,
        ExecutionLifecycleTransitionKind kind, ExecutionLifecycleTerminalReason reason = ExecutionLifecycleTerminalReason.None) =>
        new(Binding, expected, id, kind, reason, "gen-1", "fence");

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        public TemporaryDatabase() => Directory.CreateDirectory(DirectoryPath);

        public string DirectoryPath { get; } =
            Path.Combine(Path.GetTempPath(), "hufu-lifecycle-tests-" + Guid.NewGuid().ToString("N"));
        public string DatabasePath => Path.Combine(DirectoryPath, "lifecycle.db");

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}

