using Penghou.Hufu;
using Penghou.Hufu.Sqlite;
using Xunit;

namespace Penghou.Hufu.Tests;

/// <summary>
/// Delegation-visibility authority: idempotent publish commands, terminal
/// revocation, and validity-window checks over one slot per
/// <c>(tenant, subject, delegation, generation)</c>. Same-slot content
/// changes conflict deterministically, including across process-equivalent
/// store reopening. Behavioral contract migrated from the proven
/// Marang-side implementation without semantic change.
/// </summary>
public sealed class DelegationVisibilityTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Publish_Replays_And_Conflicts_With_Persistence_Across_Reopen()
    {
        var path = NewDatabasePath();
        try
        {
            var store = new SqliteDelegationVisibilityStore(path);
            var candidate = Binding("key-1", new string('a', 64));

            var published = await store.PublishAsync(candidate);
            Assert.Equal(DelegationVisibilityPublishStatus.Published, published.Status);

            var replayed = await store.PublishAsync(candidate with { });
            Assert.Equal(DelegationVisibilityPublishStatus.Replayed, replayed.Status);
            Assert.Equal(published.Binding, replayed.Binding);

            var conflicted = await store.PublishAsync(candidate with { FingerprintHash = new string('b', 64) });
            Assert.Equal(DelegationVisibilityPublishStatus.Conflict, conflicted.Status);

            // A fresh store object over the same file observes identical state.
            var reopened = new SqliteDelegationVisibilityStore(path);
            var afterReopen = await reopened.PublishAsync(candidate with { });
            Assert.Equal(DelegationVisibilityPublishStatus.Replayed, afterReopen.Status);
            Assert.Equal(published.Binding, afterReopen.Binding);
            var conflictAfterReopen = await reopened.PublishAsync(candidate with { FingerprintHash = new string('b', 64) });
            Assert.Equal(DelegationVisibilityPublishStatus.Conflict, conflictAfterReopen.Status);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task Revocation_Is_Terminal_With_Idempotent_Replay()
    {
        var path = NewDatabasePath();
        try
        {
            var store = new SqliteDelegationVisibilityStore(path);
            var candidate = Binding("key-1", new string('a', 64));
            await store.PublishAsync(candidate);

            Assert.Equal(
                DelegationVisibilityCheckStatus.Granted,
                await store.CheckAsync("tenant", "subject", candidate.DelegationId, "g0", DelegationVisibilityOperation.Observe, Start));
            Assert.Equal(
                DelegationVisibilityCheckStatus.Unknown,
                await store.CheckAsync("tenant", "stranger", candidate.DelegationId, "g0", DelegationVisibilityOperation.Observe, Start));

            Assert.Equal(
                DelegationVisibilityRevokeStatus.Revoked,
                await store.RevokeAsync("tenant", "subject", candidate.DelegationId, "g0", "revoke-1", "operator.revoked"));
            Assert.Equal(
                DelegationVisibilityRevokeStatus.Replayed,
                await store.RevokeAsync("tenant", "subject", candidate.DelegationId, "g0", "revoke-1", "operator.revoked"));
            Assert.Equal(
                DelegationVisibilityRevokeStatus.Conflict,
                await store.RevokeAsync("tenant", "subject", candidate.DelegationId, "g0", "revoke-2", "operator.revoked"));
            Assert.Equal(
                DelegationVisibilityRevokeStatus.NotFound,
                await store.RevokeAsync("tenant", "subject", "no-such-delegation", "g0", "revoke-3", "operator.revoked"));

            Assert.Equal(
                DelegationVisibilityCheckStatus.Revoked,
                await store.CheckAsync("tenant", "subject", candidate.DelegationId, "g0", DelegationVisibilityOperation.Observe, Start));
            var afterRevoke = await store.PublishAsync(candidate with { });
            Assert.Equal(DelegationVisibilityPublishStatus.Revoked, afterRevoke.Status);

            // Terminal state survives reopening.
            var reopened = new SqliteDelegationVisibilityStore(path);
            Assert.Equal(
                DelegationVisibilityCheckStatus.Revoked,
                await reopened.CheckAsync("tenant", "subject", candidate.DelegationId, "g0", DelegationVisibilityOperation.Observe, Start));
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task Check_Distinguishes_Expiry_And_Forbidden()
    {
        var path = NewDatabasePath();
        try
        {
            var store = new SqliteDelegationVisibilityStore(path);
            var candidate = Binding("key-1", new string('a', 64));
            await store.PublishAsync(candidate);

            Assert.Equal(
                DelegationVisibilityCheckStatus.Expired,
                await store.CheckAsync("tenant", "subject", candidate.DelegationId, "g0",
                    DelegationVisibilityOperation.Observe, candidate.ExpiresAt.AddSeconds(1)));
            Assert.Equal(
                DelegationVisibilityCheckStatus.Forbidden,
                await store.CheckAsync("tenant", "subject", candidate.DelegationId, "g0",
                    (DelegationVisibilityOperation)16, Start));
            Assert.Equal(
                DelegationVisibilityCheckStatus.Forbidden,
                await store.CheckAsync("tenant", "subject", candidate.DelegationId, "g0",
                    DelegationVisibilityOperation.Intervene, Start));
        }
        finally
        {
            Delete(path);
        }
    }

    private static DelegationVisibilityBinding Binding(string requestKey, string fingerprintHash)
    {
        var delegationId = "delegation-" + requestKey;
        return new DelegationVisibilityBinding(
            "tenant", "subject", delegationId, "g0", requestKey,
            "v3", fingerprintHash,
            DelegationVisibilityOperation.Observe | DelegationVisibilityOperation.Cancel,
            Start, Start.AddDays(30),
            $"visibility-accept/{requestKey}/g0",
            Revoked: false, RevokeReason: string.Empty, Start);
    }

    private static string NewDatabasePath() =>
        Path.Combine(Path.GetTempPath(), "hufu-visibility-" + Guid.NewGuid().ToString("N"), "visibility.db");

    private static void Delete(string path)
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
        catch
        {
        }

        var directory = Path.GetDirectoryName(path);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
