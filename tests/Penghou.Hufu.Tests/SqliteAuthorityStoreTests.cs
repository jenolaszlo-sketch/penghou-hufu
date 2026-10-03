using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Penghou.Hufu;
using Penghou.Hufu.Cedar;
using Penghou.Hufu.Luban;
using Penghou.IO.Abstractions;
using Penghou.IO.Local;
using Penghou.Luban;
using Penghou.Luban.Language;
using Penghou.Hufu.Sqlite;
using Xunit;

namespace Penghou.Hufu.Tests;

public sealed class SqliteAuthorityStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly AuthenticatedAuthorityContext Context = new("tenant", "subject", "run", "revision", "fence");
    private static readonly AuthorityStoreActor Actor = new("tenant", "host", "session-1");
    private static readonly AuthorityStoreActor OtherActor = new("tenant", "other-host", "session-2");
    private static readonly AuthorityScope WorkspaceScope = new("workspace", "", AuthorityScopeKind.Subtree);

    [Fact]
    public async Task PublishedSnapshotIsCurrentAfterReopenAndExactReplayDoesNotAdvanceHead()
    {
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        var gate = new RecordingAuthorizer(Actor);
        var store = database.Open(gate, time);
        var snapshot = Snapshot(Context, "v1");
        var publish = Publish("publish-1", snapshot, expectedSequence: 0);

        var applied = await store.PublishAsync(publish);
        var replayGateActor = Actor with { SessionId = "session-rotated" };
        gate.Result = new(AuthorityStatus.Permit, replayGateActor);
        var replayed = await store.PublishAsync(publish with { Actor = replayGateActor });
        gate.Result = new(AuthorityStatus.Permit, Actor);
        var current = await store.ReadCurrentAsync(Actor, Context);

        Assert.Equal(AuthorityMutationStatus.Applied, applied.Status);
        Assert.Equal(1, applied.Record!.Sequence);
        Assert.Equal(AuthorityMutationStatus.Replayed, replayed.Status);
        Assert.Equal(applied.Record!.Sequence, replayed.Record!.Sequence);
        Assert.Equal(applied.Record.Snapshot!.Identity, replayed.Record.Snapshot!.Identity);
        Assert.Equal(applied.Record.Actor, replayed.Record.Actor);
        Assert.Equal(Actor.SessionId, replayed.Record!.Actor.SessionId);
        Assert.Equal(AuthorityReadStatus.Active, current.Status);
        Assert.Equal(1, current.Record!.Sequence);
        Assert.Equal(snapshot.Identity, current.Record.Snapshot!.Identity);

        var reopened = database.Open(gate, time);
        var afterRestart = await reopened.ReadCurrentAsync(Actor, Context);
        Assert.Equal(AuthorityReadStatus.Active, afterRestart.Status);
        Assert.Equal(current.Record!.Sequence, afterRestart.Record!.Sequence);
        Assert.Equal(current.Record.Snapshot!.Identity, afterRestart.Record.Snapshot!.Identity);
    }

    [Fact]
    public async Task StaleConcurrentPublishersCannotBothAdvanceCurrentSequence()
    {
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        var gate = new RecordingAuthorizer(Actor);
        var store = database.Open(gate, time);
        using var barrier = new Barrier(2);
        var first = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await database.Open(gate, time).PublishAsync(Publish("publish-a", Snapshot(Context, "v-a"), 0));
        });
        var second = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await database.Open(gate, time).PublishAsync(Publish("publish-b", Snapshot(Context, "v-b"), 0));
        });

        var outcomes = await Task.WhenAll(first, second);

        Assert.Single(outcomes, outcome => outcome.Status == AuthorityMutationStatus.Applied);
        Assert.Single(outcomes, outcome => outcome.Status == AuthorityMutationStatus.Conflict);
        var current = await store.ReadCurrentAsync(Actor, Context);
        Assert.Equal(AuthorityReadStatus.Active, current.Status);
        Assert.Equal(1, current.Record!.Sequence);
        var history = await store.ReadHistoryAsync(Actor, AuthoritySubject.From(Context), 32);
        Assert.Equal(AuthorityReadStatus.Active, history.Status);
        Assert.Single(history.Records);
    }

    [Fact]
    public async Task ConcurrentPublishAndRevokeHaveOneLinearizedWinner()
    {
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        var store = database.Open(new RecordingAuthorizer(Actor), time);
        await store.PublishAsync(Publish("initial", Snapshot(Context, "v1"), 0));

        using var barrier = new Barrier(2);
        var publish = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await database.Open(new RecordingAuthorizer(Actor), time)
                .PublishAsync(Publish("advance", Snapshot(Context, "v2"), 1));
        });
        var revoke = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await database.Open(new RecordingAuthorizer(Actor), time)
                .RevokeAsync(new("revoke", Actor, Context, 1, "host.revoked"));
        });
        var outcomes = await Task.WhenAll(publish, revoke);

        Assert.Single(outcomes, outcome => outcome.Status == AuthorityMutationStatus.Applied);
        Assert.Single(outcomes, outcome => outcome.Status == AuthorityMutationStatus.Conflict);
        var current = await store.ReadCurrentAsync(Actor, Context);
        Assert.Equal(2, current.Record!.Sequence);
        Assert.True(current.Status is AuthorityReadStatus.Active or AuthorityReadStatus.Revoked);
        if (current.Status == AuthorityReadStatus.Revoked)
            Assert.Null(current.Record.Snapshot);
        else
            Assert.Equal("v2", current.Record.Snapshot!.Version);
    }

    [Fact]
    public async Task RevokeCanTombstoneMissingRunAndCannotBeUndoneByPublishOrReplay()
    {
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        var gate = new RecordingAuthorizer(Actor);
        var store = database.Open(gate, time);
        var revoke = new AuthorityRevokeCommand("revoke-1", Actor, Context, 0, "host.revoked");

        var revoked = await store.RevokeAsync(revoke);
        var replayed = await store.RevokeAsync(revoke);
        var afterPublish = await store.PublishAsync(Publish("publish-after-revoke", Snapshot(Context, "v1"), 1));
        var current = await store.ReadCurrentAsync(Actor, Context);

        Assert.Equal(AuthorityMutationStatus.Applied, revoked.Status);
        Assert.Equal(1, revoked.Record!.Sequence);
        Assert.Equal(AuthorityMutationStatus.Replayed, replayed.Status);
        Assert.Equal(revoked.Record, replayed.Record);
        Assert.Equal(AuthorityMutationStatus.Conflict, afterPublish.Status);
        Assert.Equal(AuthorityReadStatus.Revoked, current.Status);
        Assert.Null(current.Record!.Snapshot);
        var history = await store.ReadHistoryAsync(Actor, AuthoritySubject.From(Context), 8);
        Assert.Equal(AuthorityReadStatus.Active, history.Status);
        Assert.Single(history.Records);
    }

    [Fact]
    public async Task PublishingNewContextMakesPreviousRevisionAndFenceStaleWithoutHistoryFallback()
    {
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        var gate = new RecordingAuthorizer(Actor);
        var store = database.Open(gate, time);
        var newer = Context with { RevisionId = "revision-2", FenceId = "fence-2" };
        await store.PublishAsync(Publish("publish-old", Snapshot(Context, "v1"), 0));
        var publishNew = await store.PublishAsync(Publish("publish-new", Snapshot(newer, "v2"), 1));

        var staleRead = await store.ReadCurrentAsync(Actor, Context);
        var currentRead = await store.ReadCurrentAsync(Actor, newer);
        var oldReplay = await store.PublishAsync(Publish("publish-old", Snapshot(Context, "v1"), 0));
        var restoreOldContext = await store.PublishAsync(Publish("restore-old-context", Snapshot(Context, "v3"), 2));
        var afterReplay = await store.ReadCurrentAsync(Actor, newer);

        Assert.Equal(AuthorityMutationStatus.Applied, publishNew.Status);
        Assert.Equal(AuthorityReadStatus.StaleContext, staleRead.Status);
        Assert.Null(staleRead.Record);
        Assert.Equal(AuthorityReadStatus.Active, currentRead.Status);
        Assert.Equal(newer, currentRead.Record!.Context);
        Assert.Equal(2, currentRead.Record.Sequence);
        Assert.Equal(AuthorityMutationStatus.Replayed, oldReplay.Status);
        Assert.Equal(AuthorityMutationStatus.Conflict, restoreOldContext.Status);
        Assert.Equal(2, afterReplay.Record!.Sequence);
        Assert.Equal(newer, afterReplay.Record.Context);
    }

    [Fact]
    public async Task ReusingCommandIdentityWithChangedIntentConflictsAcrossCommandKinds()
    {
        using var database = new TemporaryDatabase();
        var store = database.Open(new RecordingAuthorizer(Actor), new MutableTimeProvider(Now));
        var originalSnapshot = Snapshot(Context, "v1");
        var applied = await store.PublishAsync(Publish("shared-command", originalSnapshot, 0));

        var changedPublish = await store.PublishAsync(Publish("shared-command", Snapshot(Context, "v2"), 0));
        var crossKind = await store.RevokeAsync(new("shared-command", Actor, Context, 1, "revoked"));
        var duplicateVersion = await store.PublishAsync(Publish("new-command-same-version", originalSnapshot, 1));

        Assert.Equal(AuthorityMutationStatus.Applied, applied.Status);
        Assert.Equal(AuthorityMutationStatus.Conflict, changedPublish.Status);
        Assert.Equal(AuthorityMutationStatus.Conflict, crossKind.Status);
        Assert.Equal(AuthorityMutationStatus.Conflict, duplicateVersion.Status);
        var current = await store.ReadCurrentAsync(Actor, Context);
        Assert.Equal(1, current.Record!.Sequence);
        Assert.Equal(originalSnapshot.Identity, current.Record.Snapshot!.Identity);
    }

    [Fact]
    public async Task EveryValidOperationIncludingReplayAndHistoryRequiresFreshStoreAuthorization()
    {
        using var database = new TemporaryDatabase();
        var gate = new RecordingAuthorizer(Actor);
        var store = database.Open(gate, new MutableTimeProvider(Now));
        var command = Publish("publish-1", Snapshot(Context, "v1"), 0);
        var applied = await store.PublishAsync(command);
        Assert.Equal(AuthorityMutationStatus.Applied, applied.Status);

        var authorizedCount = gate.Requests.Count;
        var replay = await store.PublishAsync(command);
        var current = await store.ReadCurrentAsync(Actor, Context);
        var history = await store.ReadHistoryAsync(Actor, AuthoritySubject.From(Context), 2);
        var decision = await store.ReadDecisionAsync(Actor, AuthoritySubject.From(Context), "missing-decision");

        Assert.Equal(AuthorityMutationStatus.Replayed, replay.Status);
        Assert.Equal(AuthorityReadStatus.Active, current.Status);
        Assert.Equal(AuthorityReadStatus.Active, history.Status);
        Assert.Equal(AuthorityReadStatus.NotFound, decision.Status);
        Assert.Equal(authorizedCount + 4, gate.Requests.Count);
        Assert.Contains(gate.Requests, request => request.Operation == AuthorityStoreOperation.ReadCurrent);
        Assert.Contains(gate.Requests, request => request.Operation == AuthorityStoreOperation.ReadHistory);
        Assert.Contains(gate.Requests, request => request.Operation == AuthorityStoreOperation.ReadDecisions);
    }

    [Fact]
    public async Task AuthorizationDenialHappensBeforeOpeningOrCreatingDatabase()
    {
        using var database = new TemporaryDatabase(createDirectory: false);
        var gate = new RecordingAuthorizer(Actor) { Result = new(AuthorityStatus.Deny, Actor) };
        var store = database.Open(gate, new MutableTimeProvider(Now));

        var result = await store.PublishAsync(Publish("publish-denied", Snapshot(Context, "v1"), 0));

        Assert.Equal(AuthorityMutationStatus.Denied, result.Status);
        Assert.Single(gate.Requests);
        Assert.False(Directory.Exists(database.DirectoryPath));
    }

    [Fact]
    public async Task InvalidAuthorizerStatusOrActorAndAuthorizerFailureNeverPermitOperation()
    {
        using var database = new TemporaryDatabase();
        var mismatch = new RecordingAuthorizer(Actor) { Result = new(AuthorityStatus.Permit, OtherActor) };
        var store = database.Open(mismatch, new MutableTimeProvider(Now));

        var mismatchResult = await store.PublishAsync(Publish("mismatch", Snapshot(Context, "v1"), 0));
        mismatch.Throw = true;
        var failureResult = await store.PublishAsync(Publish("failure", Snapshot(Context, "v2"), 0));

        Assert.Equal(AuthorityMutationStatus.Unavailable, mismatchResult.Status);
        Assert.Equal(AuthorityMutationStatus.Unavailable, failureResult.Status);
        Assert.False(File.Exists(database.DatabasePath));
    }

    [Fact]
    public async Task CrossTenantActorIsDeniedBeforeIoForEveryMethodAndUnknownGateStatusFailsClosed()
    {
        using var database = new TemporaryDatabase(createDirectory: false);
        var foreignActor = Actor with { TenantId = "other-tenant" };
        var gate = new RecordingAuthorizer(Actor) { Result = new(AuthorityStatus.Permit, Actor) };
        var store = database.Open(gate, new MutableTimeProvider(Now));
        var snapshot = Snapshot(Context, "v1");
        var decision = PermitRecord("decision-cross-tenant", Context, snapshot, Now);

        var publish = await store.PublishAsync(new("publish-cross-tenant", foreignActor, snapshot, 0));
        var revoke = await store.RevokeAsync(new("revoke-cross-tenant", foreignActor, Context, 0, "revoked"));
        var current = await store.ReadCurrentAsync(foreignActor, Context);
        var history = await store.ReadHistoryAsync(foreignActor, AuthoritySubject.From(Context), 1);
        var evidence = await store.RecordDecisionAsync(foreignActor, decision);
        var readDecision = await store.ReadDecisionAsync(foreignActor, AuthoritySubject.From(Context), decision.CommandId);

        Assert.Equal(AuthorityMutationStatus.Denied, publish.Status);
        Assert.Equal(AuthorityMutationStatus.Denied, revoke.Status);
        Assert.Equal(AuthorityReadStatus.Denied, current.Status);
        Assert.Equal(AuthorityReadStatus.Denied, history.Status);
        Assert.Equal(AuthorityEvidenceStatus.Denied, evidence.Status);
        Assert.Equal(AuthorityReadStatus.Denied, readDecision.Status);
        Assert.Empty(gate.Requests); // Tenant mismatch is rejected before calling the host gate or opening SQLite.
        Assert.False(Directory.Exists(database.DirectoryPath));

        gate.Result = new((AuthorityStatus)12345, Actor);
        var invalidGateResult = await store.PublishAsync(Publish("unknown-gate-status", snapshot, 0));
        Assert.Equal(AuthorityMutationStatus.Unavailable, invalidGateResult.Status);
        Assert.Single(gate.Requests);
        Assert.False(Directory.Exists(database.DirectoryPath));
    }

    [Fact]
    public async Task PermitEvidenceIsDurablyRecordedAndExactReplayIsHistoryOnly()
    {
        using var database = new TemporaryDatabase();
        var store = database.Open(new RecordingAuthorizer(Actor), new MutableTimeProvider(Now));
        var snapshot = Snapshot(Context, "v1");
        await store.PublishAsync(Publish("publish-1", snapshot, 0));
        var record = PermitRecord("decision-1", Context, snapshot, Now);

        var written = await store.RecordDecisionAsync(Actor, record);
        var replayed = await store.RecordDecisionAsync(Actor, record);
        var read = await store.ReadDecisionAsync(Actor, AuthoritySubject.From(Context), record.CommandId);

        Assert.Equal(AuthorityEvidenceStatus.Recorded, written.Status);
        Assert.Equal(AuthorityEvidenceStatus.Replayed, replayed.Status);
        Assert.Equal(written.Entry, replayed.Entry);
        Assert.Equal(AuthorityReadStatus.Active, read.Status);
        Assert.Equal(record.Decision, read.Entry!.Record.Decision);
        Assert.Equal(1, read.Entry.SnapshotSequence);
    }

    [Fact]
    public async Task PermitCannotBeRecordedOrReplayedAfterAuthorityAdvancesOrRevokes()
    {
        using var database = new TemporaryDatabase();
        var store = database.Open(new RecordingAuthorizer(Actor), new MutableTimeProvider(Now));
        var snapshot = Snapshot(Context, "v1");
        await store.PublishAsync(Publish("publish-1", snapshot, 0));
        var record = PermitRecord("decision-1", Context, snapshot, Now);
        Assert.Equal(AuthorityEvidenceStatus.Recorded, (await store.RecordDecisionAsync(Actor, record)).Status);
        await store.RevokeAsync(new("revoke-1", Actor, Context, 1, "revoked"));

        var staleReplay = await store.RecordDecisionAsync(Actor, record);
        var historyOnly = await store.ReadDecisionAsync(Actor, AuthoritySubject.From(Context), record.CommandId);

        Assert.Equal(AuthorityEvidenceStatus.StaleAuthority, staleReplay.Status);
        Assert.Equal(AuthorityReadStatus.Active, historyOnly.Status);
        Assert.Equal(record.Decision, historyOnly.Entry!.Record.Decision);
    }

    [Theory]
    [InlineData("expiry")]
    [InlineData("not-before")]
    public async Task PermitIsRejectedWhenTheActiveGrantSetChangesAfterEvaluation(string transition)
    {
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        var store = database.Open(new RecordingAuthorizer(Actor), time);
        var grant = transition == "expiry"
            ? Grant("time-bound", Now.AddMinutes(-1), Now.AddMinutes(1))
            : Grant("not-yet-valid", Now.AddMinutes(1), Now.AddMinutes(10));
        var snapshot = Snapshot(Context, "v1", grant);
        await store.PublishAsync(Publish("publish-1", snapshot, 0));
        var evaluatedAt = transition == "expiry" ? Now : Now;
        var record = PermitRecord("decision-1", Context, snapshot, evaluatedAt);
        time.Now = Now.AddMinutes(2);

        var result = await store.RecordDecisionAsync(Actor, record);

        Assert.Equal(AuthorityEvidenceStatus.StaleAuthority, result.Status);
        var history = await store.ReadDecisionAsync(Actor, AuthoritySubject.From(Context), record.CommandId);
        Assert.Equal(AuthorityReadStatus.NotFound, history.Status);
    }

    [Fact]
    public async Task PermitRequiresEvaluationTimeWithinPublicationAndCommitWindow()
    {
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        var store = database.Open(new RecordingAuthorizer(Actor), time);
        var snapshot = Snapshot(Context, "v1");
        await store.PublishAsync(Publish("publish-1", snapshot, 0));

        var future = await store.RecordDecisionAsync(Actor, PermitRecord("future", Context, snapshot, Now.AddMinutes(1)));
        var commitBeforeEvaluation = await store.RecordDecisionAsync(Actor, PermitRecord("future-commit", Context, snapshot, Now.AddMinutes(-1)));

        Assert.Equal(AuthorityEvidenceStatus.InvalidRequest, future.Status);
        Assert.Equal(AuthorityEvidenceStatus.StaleAuthority, commitBeforeEvaluation.Status);
    }

    [Fact]
    public async Task PermitThatExpiresDuringRecordingIsNotCommitted()
    {
        using var database = new TemporaryDatabase();
        var time = new SequencedTimeProvider(Now);
        var store = database.Open(new RecordingAuthorizer(Actor), time);
        var snapshot = Snapshot(Context, "expires-during-write", validUntil: Now.AddHours(1));
        await store.PublishAsync(Publish("publish-short-lived", snapshot, 0));
        var record = PermitRecord("permit-expires-during-write", Context, snapshot, Now);

        time.SetSequence(Now, Now.AddHours(2));
        var result = await store.RecordDecisionAsync(Actor, record);
        var receipt = await store.ReadDecisionAsync(Actor, AuthoritySubject.From(Context), record.CommandId);

        Assert.Equal(AuthorityEvidenceStatus.StaleAuthority, result.Status);
        Assert.Equal(AuthorityReadStatus.NotFound, receipt.Status);
    }

    [Fact]
    public async Task PermitReplayIsNotAcknowledgedIfItExpiresDuringLookup()
    {
        using var database = new TemporaryDatabase();
        var time = new SequencedTimeProvider(Now);
        var store = database.Open(new RecordingAuthorizer(Actor), time);
        var snapshot = Snapshot(Context, "expires-during-replay", validUntil: Now.AddHours(1));
        await store.PublishAsync(Publish("publish-short-lived", snapshot, 0));
        var record = PermitRecord("permit-expires-during-replay", Context, snapshot, Now);
        Assert.Equal(AuthorityEvidenceStatus.Recorded, (await store.RecordDecisionAsync(Actor, record)).Status);

        time.SetSequence(Now, Now.AddHours(2));
        var replay = await store.RecordDecisionAsync(Actor, record);
        var originalReceipt = await store.ReadDecisionAsync(Actor, AuthoritySubject.From(Context), record.CommandId);

        Assert.Equal(AuthorityEvidenceStatus.StaleAuthority, replay.Status);
        Assert.Equal(AuthorityReadStatus.Active, originalReceipt.Status);
    }

    [Fact]
    public async Task ExpiredSnapshotCannotReplayPermitButRemainsAvailableForAttributableDenial()
    {
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        var store = database.Open(new RecordingAuthorizer(Actor), time);
        var snapshot = Snapshot(Context, "short-lived", validUntil: Now.AddHours(1));
        await store.PublishAsync(Publish("publish-short-lived", snapshot, 0));
        var permit = PermitRecord("permit-before-expiry", Context, snapshot, Now);
        Assert.Equal(AuthorityEvidenceStatus.Recorded, (await store.RecordDecisionAsync(Actor, permit)).Status);

        time.Now = Now.AddHours(2);
        var expiredRead = await store.ReadCurrentAsync(Actor, Context);
        var replay = await store.RecordDecisionAsync(Actor, permit);
        var source = new AuthorityStoreSnapshotSource(store, Actor);
        var attributedSnapshot = await source.GetCurrentAsync(Context);
        var denial = DecisionRecord("expired-denial", Context, snapshot,
            new(AuthorityStatus.Deny, "authority.snapshot-expired", snapshot.Version, "test-evaluator-v1", snapshot.Identity),
            time.Now);
        var denialWrite = await store.RecordDecisionAsync(Actor, denial);

        Assert.Equal(AuthorityReadStatus.Expired, expiredRead.Status);
        Assert.Equal(AuthorityEvidenceStatus.StaleAuthority, replay.Status);
        Assert.NotNull(attributedSnapshot);
        Assert.Equal(snapshot.Identity, attributedSnapshot.Identity);
        Assert.Equal(AuthorityEvidenceStatus.Recorded, denialWrite.Status);
    }

    [Fact]
    public async Task DecisionCommandIdentityConflictsOnRequestEvidenceAndOtherSubjectsInSameTenant()
    {
        using var database = new TemporaryDatabase();
        var store = database.Open(new RecordingAuthorizer(Actor), new MutableTimeProvider(Now));
        var snapshot = Snapshot(Context, "v1");
        await store.PublishAsync(Publish("publish-1", snapshot, 0));
        var original = PermitRecord("tenant-wide-decision-id", Context, snapshot, Now);
        Assert.Equal(AuthorityEvidenceStatus.Recorded, (await store.RecordDecisionAsync(Actor, original)).Status);

        var changedRequest = original with
        {
            Request = original.Request with { RequestIdentity = "different-request" }
        };
        var changedEvidence = original with { EvidenceJson = "{\"source\":\"different-host-evidence\"}" };
        var otherSubjectContext = Context with { SubjectId = "subject-other" };
        var otherSubjectSnapshot = Snapshot(otherSubjectContext, "other-v1");
        await store.PublishAsync(Publish("publish-other-subject", otherSubjectSnapshot, 0));
        var otherSubjectRecord = PermitRecord("tenant-wide-decision-id", otherSubjectContext, otherSubjectSnapshot, Now);

        Assert.Equal(AuthorityEvidenceStatus.Conflict, (await store.RecordDecisionAsync(Actor, changedRequest)).Status);
        Assert.Equal(AuthorityEvidenceStatus.Conflict, (await store.RecordDecisionAsync(Actor, changedEvidence)).Status);
        Assert.Equal(AuthorityEvidenceStatus.Conflict, (await store.RecordDecisionAsync(Actor, otherSubjectRecord)).Status);
        var originalRead = await store.ReadDecisionAsync(Actor, AuthoritySubject.From(Context), original.CommandId);
        Assert.Equal(AuthorityReadStatus.Active, originalRead.Status);
        Assert.Equal(original, originalRead.Entry!.Record);
    }

    [Fact]
    public async Task HistoryPagesAreExclusiveAndCancellationPreventsAuthorizationAndDatabaseIo()
    {
        using var database = new TemporaryDatabase();
        var gate = new RecordingAuthorizer(Actor);
        var store = database.Open(gate, new MutableTimeProvider(Now));
        await store.PublishAsync(Publish("publish-1", Snapshot(Context, "v1"), 0));
        await store.PublishAsync(Publish("publish-2", Snapshot(Context, "v2"), 1));
        await store.PublishAsync(Publish("publish-3", Snapshot(Context, "v3"), 2));

        var first = await store.ReadHistoryAsync(Actor, AuthoritySubject.From(Context), 2);
        var second = await store.ReadHistoryAsync(Actor, AuthoritySubject.From(Context), 2, first.NextBeforeSequence);
        var calls = gate.Requests.Count;
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await store.ReadHistoryAsync(Actor, AuthoritySubject.From(Context), 2, cancellationToken: canceled.Token));

        Assert.Equal(AuthorityReadStatus.Active, first.Status);
        Assert.Equal([3L, 2L], first.Records.Select(record => record.Sequence));
        Assert.Equal(2, first.NextBeforeSequence);
        Assert.Equal(AuthorityReadStatus.Active, second.Status);
        Assert.Equal([1L], second.Records.Select(record => record.Sequence));
        Assert.Null(second.NextBeforeSequence);
        Assert.Equal(calls, gate.Requests.Count);
    }

    [Fact]
    public async Task ForeignNonemptyDatabaseIsNeverClaimedOrOverwritten()
    {
        using var database = new TemporaryDatabase();
        byte[] foreignBytes = [0x4c, 0x55, 0x42, 0x41, 0x4e, 0x00, 0xff, 0x17];
        await File.WriteAllBytesAsync(database.DatabasePath, foreignBytes);
        var store = database.Open(new RecordingAuthorizer(Actor), new MutableTimeProvider(Now));

        var read = await store.ReadCurrentAsync(Actor, Context);

        Assert.Equal(AuthorityReadStatus.Unavailable, read.Status);
        Assert.Equal(foreignBytes, await File.ReadAllBytesAsync(database.DatabasePath));
    }

    [Fact]
    public async Task DenialsRemainHistoricalEvidenceAfterAuthorityMoves()
    {
        using var database = new TemporaryDatabase();
        var store = database.Open(new RecordingAuthorizer(Actor), new MutableTimeProvider(Now));
        var oldSnapshot = Snapshot(Context, "v1");
        await store.PublishAsync(Publish("publish-1", oldSnapshot, 0));
        var denial = DecisionRecord("denial-1", Context, oldSnapshot,
            new(AuthorityStatus.Deny, "cedar.denied", oldSnapshot.Version, "evaluator-v1", oldSnapshot.Identity), Now);
        await store.PublishAsync(Publish("publish-2", Snapshot(Context, "v2"), 1));

        var written = await store.RecordDecisionAsync(Actor, denial);
        var read = await store.ReadDecisionAsync(Actor, AuthoritySubject.From(Context), denial.CommandId);

        Assert.Equal(AuthorityEvidenceStatus.Recorded, written.Status);
        Assert.Equal(AuthorityReadStatus.Active, read.Status);
        Assert.Equal(AuthorityStatus.Deny, read.Entry!.Record.Decision.Status);
        Assert.Equal(1, read.Entry.SnapshotSequence);
    }

    [Fact]
    public async Task InvalidAndDuplicatePropertyEvidenceIsRejectedBeforePersistence()
    {
        using var database = new TemporaryDatabase();
        var store = database.Open(new RecordingAuthorizer(Actor), new MutableTimeProvider(Now));
        var snapshot = Snapshot(Context, "v1");
        await store.PublishAsync(Publish("publish-1", snapshot, 0));

        var malformed = DecisionRecord("bad-json", Context, snapshot, PermitDecision(snapshot), Now) with { EvidenceJson = "[]" };
        var duplicateNested = DecisionRecord("duplicate-json", Context, snapshot, PermitDecision(snapshot), Now)
            with { EvidenceJson = "{\"outer\":{\"field\":1,\"field\":2}}" };
        var invalidUtf8Shape = DecisionRecord("bad-evidence", Context, snapshot, PermitDecision(snapshot), Now)
            with { EvidenceFormat = "bad-\ud800" };
        var oversizeEvidence = DecisionRecord("oversize-evidence", Context, snapshot, PermitDecision(snapshot), Now)
            with { EvidenceJson = "{\"large\":\"" + new string('x', 262_145) + "\"}" };

        Assert.Equal(AuthorityEvidenceStatus.InvalidRequest, (await store.RecordDecisionAsync(Actor, malformed)).Status);
        Assert.Equal(AuthorityEvidenceStatus.InvalidRequest, (await store.RecordDecisionAsync(Actor, duplicateNested)).Status);
        Assert.Equal(AuthorityEvidenceStatus.InvalidRequest, (await store.RecordDecisionAsync(Actor, invalidUtf8Shape)).Status);
        Assert.Equal(AuthorityEvidenceStatus.InvalidRequest, (await store.RecordDecisionAsync(Actor, oversizeEvidence)).Status);
        Assert.Equal(AuthorityReadStatus.NotFound,
            (await store.ReadDecisionAsync(Actor, AuthoritySubject.From(Context), "bad-json")).Status);
    }

    [Fact]
    public async Task EventAndDecisionCapacitiesFailClosedWithoutChangingAuthority()
    {
        using var eventsDb = new TemporaryDatabase();
        var eventStore = eventsDb.Open(new RecordingAuthorizer(Actor), new MutableTimeProvider(Now),
            new() { MaxAuthorityEvents = 1, MaxDecisionEntries = 10, MaxStoredBytes = 1_000_000 });
        await eventStore.PublishAsync(Publish("publish-1", Snapshot(Context, "v1"), 0));
        var secondEvent = await eventStore.PublishAsync(Publish("publish-2", Snapshot(Context, "v2"), 1));
        Assert.Equal(AuthorityMutationStatus.CapacityExceeded, secondEvent.Status);
        Assert.Equal(1, (await eventStore.ReadCurrentAsync(Actor, Context)).Record!.Sequence);

        using var decisionsDb = new TemporaryDatabase();
        var decisionStore = decisionsDb.Open(new RecordingAuthorizer(Actor), new MutableTimeProvider(Now),
            new() { MaxAuthorityEvents = 10, MaxDecisionEntries = 1, MaxStoredBytes = 1_000_000 });
        var snapshot = Snapshot(Context, "v1");
        await decisionStore.PublishAsync(Publish("publish-1", snapshot, 0));
        var first = await decisionStore.RecordDecisionAsync(Actor, PermitRecord("decision-1", Context, snapshot, Now));
        var second = await decisionStore.RecordDecisionAsync(Actor, PermitRecord("decision-2", Context, snapshot, Now,
            requestIdentity: "request-2"));
        Assert.Equal(AuthorityEvidenceStatus.Recorded, first.Status);
        Assert.Equal(AuthorityEvidenceStatus.CapacityExceeded, second.Status);
        Assert.Equal(AuthorityReadStatus.NotFound,
            (await decisionStore.ReadDecisionAsync(Actor, AuthoritySubject.From(Context), "decision-2")).Status);

        using var bytesDb = new TemporaryDatabase();
        var byteLimitedStore = bytesDb.Open(new RecordingAuthorizer(Actor), new MutableTimeProvider(Now),
            new() { MaxAuthorityEvents = 10, MaxDecisionEntries = 10, MaxStoredBytes = 1 });
        var tooLarge = await byteLimitedStore.PublishAsync(Publish("publish-too-large", Snapshot(Context, "v1"), 0));
        Assert.Equal(AuthorityMutationStatus.CapacityExceeded, tooLarge.Status);
        Assert.Equal(AuthorityReadStatus.NotFound,
            (await byteLimitedStore.ReadCurrentAsync(Actor, Context)).Status);
    }

    [Fact]
    public async Task UnknownSchemaCorruptionAndHeadRollbackReturnUnavailableRatherThanOlderAuthority()
    {
        using var unknownDatabase = new TemporaryDatabase();
        await using (var connection = RawConnection(unknownDatabase.DatabasePath))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA application_id = 1213548117; PRAGMA user_version = 999;";
            await command.ExecuteNonQueryAsync();
        }
        var unknownStore = unknownDatabase.Open(new RecordingAuthorizer(Actor), new MutableTimeProvider(Now));
        Assert.Equal(AuthorityReadStatus.Unavailable,
            (await unknownStore.ReadCurrentAsync(Actor, Context)).Status);

        using var corruptDatabase = new TemporaryDatabase();
        var store = corruptDatabase.Open(new RecordingAuthorizer(Actor), new MutableTimeProvider(Now));
        await store.PublishAsync(Publish("publish-1", Snapshot(Context, "v1"), 0));
        await store.PublishAsync(Publish("publish-2", Snapshot(Context, "v2"), 1));
        await using (var connection = RawConnection(corruptDatabase.DatabasePath))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE hufu_head SET sequence = 1;";
            await command.ExecuteNonQueryAsync();
        }

        Assert.Equal(AuthorityReadStatus.Unavailable,
            (await corruptDatabase.Open(new RecordingAuthorizer(Actor), new MutableTimeProvider(Now))
                .ReadCurrentAsync(Actor, Context)).Status);

        using var gapDatabase = new TemporaryDatabase();
        var gapStore = gapDatabase.Open(new RecordingAuthorizer(Actor), new MutableTimeProvider(Now));
        await gapStore.PublishAsync(Publish("publish-1", Snapshot(Context, "v1"), 0));
        await gapStore.PublishAsync(Publish("publish-2", Snapshot(Context, "v2"), 1));
        await using (var connection = RawConnection(gapDatabase.DatabasePath))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys = OFF;";
            await command.ExecuteNonQueryAsync();
            command.CommandText = "DELETE FROM hufu_events WHERE sequence = 1;";
            await command.ExecuteNonQueryAsync();
        }
        Assert.Equal(AuthorityReadStatus.Unavailable,
            (await gapDatabase.Open(new RecordingAuthorizer(Actor), new MutableTimeProvider(Now))
                .ReadCurrentAsync(Actor, Context)).Status);

        using var counterDatabase = new TemporaryDatabase();
        var counterStore = counterDatabase.Open(new RecordingAuthorizer(Actor), new MutableTimeProvider(Now));
        await counterStore.PublishAsync(Publish("publish-1", Snapshot(Context, "v1"), 0));
        await using (var connection = RawConnection(counterDatabase.DatabasePath))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE hufu_usage SET event_count = 0, stored_bytes = 0 WHERE id = 1;";
            await command.ExecuteNonQueryAsync();
        }
        Assert.Equal(AuthorityReadStatus.Unavailable,
            (await counterDatabase.Open(new RecordingAuthorizer(Actor), new MutableTimeProvider(Now))
                .ReadCurrentAsync(Actor, Context)).Status);

        using var bodyCorruptDatabase = new TemporaryDatabase();
        var bodyStore = bodyCorruptDatabase.Open(new RecordingAuthorizer(Actor), new MutableTimeProvider(Now));
        await bodyStore.PublishAsync(Publish("publish-1", Snapshot(Context, "v1"), 0));
        await using (var connection = RawConnection(bodyCorruptDatabase.DatabasePath))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE hufu_events SET body = X'00';";
            await command.ExecuteNonQueryAsync();
        }
        Assert.Equal(AuthorityReadStatus.Unavailable,
            (await bodyCorruptDatabase.Open(new RecordingAuthorizer(Actor), new MutableTimeProvider(Now))
                .ReadCurrentAsync(Actor, Context)).Status);
    }

    [Fact]
    public async Task TransactionAbortDoesNotLeavePartiallyPublishedHeadOrCommand()
    {
        using var database = new TemporaryDatabase();
        var store = database.Open(new RecordingAuthorizer(Actor), new MutableTimeProvider(Now));
        await store.ReadCurrentAsync(Actor, Context); // Initialize the owned schema.
        await using (var connection = RawConnection(database.DatabasePath))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER abort_hufu_event BEFORE INSERT ON hufu_events BEGIN SELECT RAISE(ABORT, 'test abort'); END;";
            await command.ExecuteNonQueryAsync();
        }

        var result = await store.PublishAsync(Publish("publish-abort", Snapshot(Context, "v1"), 0));

        Assert.Equal(AuthorityMutationStatus.Unavailable, result.Status);
        Assert.Equal(AuthorityReadStatus.NotFound, (await store.ReadCurrentAsync(Actor, Context)).Status);
        Assert.Empty((await store.ReadHistoryAsync(Actor, AuthoritySubject.From(Context), 8)).Records);
    }

    [Fact]
    public async Task LateCommandInsertAbortRollsBackEventAndRetryOfSameCommandApplies()
    {
        using var database = new TemporaryDatabase();
        var store = database.Open(new RecordingAuthorizer(Actor), new MutableTimeProvider(Now));
        await store.ReadCurrentAsync(Actor, Context); // Initialize the owned schema.
        await using (var connection = RawConnection(database.DatabasePath))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER abort_hufu_command BEFORE INSERT ON hufu_commands BEGIN SELECT RAISE(ABORT, 'late test abort'); END;";
            await command.ExecuteNonQueryAsync();
        }

        var publish = Publish("retry-after-late-abort", Snapshot(Context, "v1"), 0);
        var aborted = await store.PublishAsync(publish);
        Assert.Equal(AuthorityMutationStatus.Unavailable, aborted.Status);

        await using (var connection = RawConnection(database.DatabasePath))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER abort_hufu_command;";
            await command.ExecuteNonQueryAsync();
        }

        var retry = await store.PublishAsync(publish);
        var current = await store.ReadCurrentAsync(Actor, Context);
        var history = await store.ReadHistoryAsync(Actor, AuthoritySubject.From(Context), 8);

        Assert.Equal(AuthorityMutationStatus.Applied, retry.Status);
        Assert.Equal(1, retry.Record!.Sequence);
        Assert.Equal(AuthorityReadStatus.Active, current.Status);
        Assert.Equal("v1", current.Record!.Snapshot!.Version);
        Assert.Single(history.Records);
    }

    [Fact]
    public async Task CurrentSourceAndDecisionRecorderCompleteRealCedarLubanRead()
    {
        if (!OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip("The Local read integration is qualified only on Windows.");
        using var workspace = new TestWorkspace();
        workspace.Write("public.txt", "hello from the SQLite authority source");
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        var gate = new RecordingAuthorizer(Actor);
        var store = database.Open(gate, time);
        var snapshot = Snapshot(Context, "v1");
        await store.PublishAsync(Publish("publish-1", snapshot, 0));
        var captureEvaluator = new CapturingCedarEvaluator(snapshot, new CedarAuthorityEvaluator());
        var factory = new CedarEvidenceFactory(captureEvaluator);
        var source = new AuthorityStoreSnapshotSource(store, Actor);
        var recorder = new AuthorityStoreDecisionRecorder(store, Actor, factory);
        var document = LanguageCompiler.Compile("read public.txt", workspace.Id);
        Assert.True(document.Succeeded, string.Join(", ", document.Diagnostics.Select(d => d.Code)));
        var invocation = new EffectInvocation("subject", "effect-1", "attempt-1");
        var authorizer = new HufuLanguageAuthorizer(Context, invocation, document.Document!, source,
            captureEvaluator, recorder, time);
        var runtime = new LanguageRuntime(new WorkspaceReference(workspace.Id.Value), new LocalWorkspaceProvider(workspace.Id, workspace.Root), authorizer);

        var result = await runtime.ExecuteAsync(invocation, document.Document!);

        Assert.Equal(LanguageRunStatus.Succeeded, result.Status);
        var content = Assert.IsType<FileContentValue>(Assert.Single(Assert.Single(result.Statements!).Values));
        Assert.Equal("hello from the SQLite authority source", content.Content);
        Assert.NotEmpty(factory.CommandIds);
        foreach (var commandId in factory.CommandIds)
            Assert.Equal(AuthorityReadStatus.Active,
                (await store.ReadDecisionAsync(Actor, AuthoritySubject.From(Context), commandId)).Status);
    }

    [Fact]
    public async Task FailedRequiredDurableRecorderBlocksRealLubanRead()
    {
        if (!OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip("The Local read integration is qualified only on Windows.");
        using var workspace = new TestWorkspace();
        const string secret = "content must not be released without durable decision evidence";
        workspace.Write("public.txt", secret);
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        var gate = new RecordingAuthorizer(Actor);
        var store = database.Open(gate, time,
            new() { MaxAuthorityEvents = 10, MaxDecisionEntries = 1, MaxStoredBytes = 1_000_000 });
        var snapshot = Snapshot(Context, "v1");
        await store.PublishAsync(Publish("publish-1", snapshot, 0));
        var captureEvaluator = new CapturingCedarEvaluator(snapshot, new CedarAuthorityEvaluator());
        var factory = new CedarEvidenceFactory(captureEvaluator);
        var authorizer = new HufuLanguageAuthorizer(Context, new EffectInvocation("subject", "effect-1", "attempt-1"),
            AssertDocument("read public.txt", workspace.Id), new AuthorityStoreSnapshotSource(store, Actor),
            captureEvaluator, new AuthorityStoreDecisionRecorder(store, Actor, factory), time);
        var runtime = new LanguageRuntime(new WorkspaceReference(workspace.Id.Value), new LocalWorkspaceProvider(workspace.Id, workspace.Root), authorizer);

        var result = await runtime.ExecuteAsync(new EffectInvocation("subject", "effect-1", "attempt-1"),
            AssertDocument("read public.txt", workspace.Id));

        Assert.Equal(LanguageRunStatus.AuthorizationUnavailable, result.Status);
        Assert.DoesNotContain(result.Statements?.SelectMany(statement => statement.Values) ?? [],
            value => value is FileContentValue file && file.Content == secret);
        var commandIds = factory.CommandIds.ToArray();
        Assert.Equal(2, commandIds.Length);
        Assert.Equal(AuthorityReadStatus.Active,
            (await store.ReadDecisionAsync(Actor, AuthoritySubject.From(Context), commandIds[0])).Status);
        Assert.Equal(AuthorityReadStatus.NotFound,
            (await store.ReadDecisionAsync(Actor, AuthoritySubject.From(Context), commandIds[1])).Status);
    }

    private static CompiledDocument AssertDocument(string source, WorkspaceId workspace)
    {
        var compilation = LanguageCompiler.Compile(source, workspace);
        Assert.True(compilation.Succeeded, string.Join(", ", compilation.Diagnostics.Select(d => d.Code)));
        return compilation.Document!;
    }

    private static AuthorityPublishCommand Publish(string commandId, AuthoritySnapshot snapshot, long expectedSequence) =>
        new(commandId, Actor, snapshot, expectedSequence);

    private static AuthoritySnapshot Snapshot(AuthenticatedAuthorityContext context, string version,
        AuthorityGrant? grant = null, DateTimeOffset? validUntil = null) => new(context, version,
            [new AuthorityLayer("run", [grant ?? Grant("read", Now.AddMinutes(-1), Now.AddDays(1))])], [], validUntil ?? Now.AddDays(1));

    private static AuthorityGrant Grant(string id, DateTimeOffset notBefore, DateTimeOffset expiresAt) =>
        new(id, [AuthorityAction.ReadFile, AuthorityAction.ReadMetadata, AuthorityAction.Release],
            WorkspaceScope, [], notBefore, expiresAt);

    private static AuthorityDecision PermitDecision(AuthoritySnapshot snapshot) =>
        new(AuthorityStatus.Permit, "test.permit", snapshot.Version, "evaluator-v1", snapshot.Identity);

    private static AuthorityDecisionRecord PermitRecord(string commandId, AuthenticatedAuthorityContext context,
        AuthoritySnapshot snapshot, DateTimeOffset evaluatedAt, string requestIdentity = "request-1") =>
        DecisionRecord(commandId, context, snapshot, PermitDecision(snapshot), evaluatedAt, requestIdentity);

    private static AuthorityDecisionRecord DecisionRecord(string commandId, AuthenticatedAuthorityContext context,
        AuthoritySnapshot snapshot, AuthorityDecision decision, DateTimeOffset evaluatedAt,
        string requestIdentity = "request-1") => new(commandId,
            new AuthorityRequest(context, AuthorityAction.ReadFile, "workspace", "public.txt", requestIdentity),
            decision, evaluatedAt, "test-evidence-v1", "{\"source\":\"host-test\"}");

    private static SqliteConnection RawConnection(string path) => new(new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Pooling = false
    }.ToString());

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly bool _deleteDirectory;
        public string DirectoryPath { get; }
        public string DatabasePath => Path.Combine(DirectoryPath, "authority.db");

        public TemporaryDatabase(bool createDirectory = true)
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "hufu-sqlite-tests-" + Guid.NewGuid().ToString("N"));
            _deleteDirectory = createDirectory;
            if (createDirectory) Directory.CreateDirectory(DirectoryPath);
        }

        public SqliteAuthorityStore Open(IAuthorityStoreAuthorizer authorizer, TimeProvider time,
            SqliteAuthorityStoreOptions? options = null) => new(DatabasePath, authorizer, time, options);

        public void Dispose()
        {
            if (_deleteDirectory && Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class SequencedTimeProvider(DateTimeOffset fallback) : TimeProvider
    {
        private readonly ConcurrentQueue<DateTimeOffset> _sequence = new();
        public void SetSequence(params DateTimeOffset[] values)
        {
            while (_sequence.TryDequeue(out _)) { }
            foreach (var value in values) _sequence.Enqueue(value);
        }
        public override DateTimeOffset GetUtcNow() => _sequence.TryDequeue(out var value) ? value : fallback;
    }

    private sealed class RecordingAuthorizer(AuthorityStoreActor expectedActor) : IAuthorityStoreAuthorizer
    {
        private readonly ConcurrentQueue<AuthorityStoreAccessRequest> _requests = new();
        public IReadOnlyCollection<AuthorityStoreAccessRequest> Requests => _requests.ToArray();
        public AuthorityStoreAuthorization Result { get; set; } = new(AuthorityStatus.Permit, expectedActor);
        public bool Throw { get; set; }

        public ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(AuthorityStoreAccessRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _requests.Enqueue(request);
            if (Throw) throw new InvalidOperationException("host authorizer unavailable");
            return ValueTask.FromResult(Result);
        }
    }

    // Test-only capture decorator: the evidence factory consumes the exact
    // EvaluateDetailed result produced for the decision; it never re-evaluates later.
    private sealed class CapturingCedarEvaluator(AuthoritySnapshot snapshot, CedarAuthorityEvaluator inner) : IAuthorityEvaluator
    {
        private readonly ConcurrentQueue<(AuthorityRequest Request, DateTimeOffset EvaluatedAt, CedarEvaluationDetails Details)> _captures = new();

        public AuthorityDecision Evaluate(AuthoritySnapshot evaluatedSnapshot, AuthorityRequest request, DateTimeOffset now)
        {
            if (evaluatedSnapshot.Identity != snapshot.Identity) return new(AuthorityStatus.Unavailable,
                "test.snapshot-mismatch", evaluatedSnapshot.Version, "test-capture-v1", evaluatedSnapshot.Identity);
            var details = inner.EvaluateDetailed(snapshot, request, now);
            _captures.Enqueue((request, now, details));
            return details.Decision;
        }

        public bool TryTake(AuthorityRequest request, out DateTimeOffset evaluatedAt, out CedarEvaluationDetails details)
        {
            if (_captures.TryDequeue(out var captured) && captured.Request == request)
            {
                evaluatedAt = captured.EvaluatedAt;
                details = captured.Details;
                return true;
            }
            evaluatedAt = default;
            details = null!;
            return false;
        }
    }

    private sealed class CedarEvidenceFactory(CapturingCedarEvaluator evaluator) : IAuthorityDecisionRecordFactory
    {
        private readonly ConcurrentQueue<string> _commands = new();
        public IReadOnlyCollection<string> CommandIds => _commands.ToArray();

        public ValueTask<AuthorityDecisionRecord?> CreateAsync(AuthorityRequest request, AuthorityDecision decision,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!evaluator.TryTake(request, out var evaluatedAt, out var details) || details.Decision != decision)
                return ValueTask.FromResult<AuthorityDecisionRecord?>(null);
            var commandId = Guid.NewGuid().ToString("N");
            _commands.Enqueue(commandId);
            var evidence = JsonSerializer.Serialize(new
            {
                schema = details.SchemaDigest,
                entities = details.EntityDigest,
                layers = details.Layers.Select(layer => new
                {
                    layer.LayerId,
                    layer.SnapshotDigest,
                    layer.SchemaDigest,
                    layer.PolicyDigest,
                    layer.EntityDigest,
                    policyValid = layer.PolicyValidation?.IsValid,
                    requestValid = layer.RequestValidation is not null,
                    cleanAllow = layer.Authorization?.IsCleanAllow
                }).ToArray()
            });
            return ValueTask.FromResult<AuthorityDecisionRecord?>(new(commandId, request, decision,
                evaluatedAt, "cedar-details-v1", evidence));
        }
    }

    private sealed class TestWorkspace : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "hufu-sqlite-luban-" + Guid.NewGuid().ToString("N"));
        public WorkspaceId Id { get; } = new("workspace");
        public TestWorkspace() => Directory.CreateDirectory(Root);
        public void Write(string relativePath, string content)
        {
            var fullPath = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content, new UTF8Encoding(false, true));
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
