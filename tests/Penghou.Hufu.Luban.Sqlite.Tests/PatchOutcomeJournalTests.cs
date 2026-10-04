using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Penghou.Hufu;
using Penghou.Hufu.Luban.Sqlite;
using Penghou.Hufu.Sqlite;
using Penghou.IO.Abstractions;
using Penghou.Luban;
using Penghou.Luban.Changes;
using Penghou.Luban.Execution;
using Penghou.Luban.Language;
using Penghou.Luban.Resolution;
using Xunit;

namespace Penghou.Hufu.Luban.Sqlite.Tests;

public sealed class PatchOutcomeJournalTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "hufu-patch-journal-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly MutableClock _clock = new(new DateTimeOffset(2026, 10, 4, 8, 0, 0, TimeSpan.Zero));
    private readonly RecordingAuthorizer _authorizer = new();
    private readonly AuthorityStoreActor _actor = new("tenant", "host", "session");
    private readonly AuthenticatedAuthorityContext _context = new("tenant", "subject", "run", "revision", "fence");
    private SqlitePatchOutcomeJournal Journal(PatchJournalOptions? options = null) => new(_path, _authorizer, _clock, options);

    [Fact]
    public async Task RealAuthorityGateCommitsOneColocatedStartAndReplayCannotDispatch()
    {
        var fixture = await Reserve("op-real-core-gate");
        var journal = Journal();
        var store = new SqliteAuthorityStore(journal, new CoreFixturePolicy(_actor));
        var command = PatchIdentity.Command(fixture.Record);
        var now = _clock.GetUtcNow();
        var snapshot = new AuthoritySnapshot(_context, "portable-snapshot", [new("layer", [new("grant", [AuthorityAction.PatchFile],
            new(command.Request.WorkspaceId, command.Request.RelativePath, AuthorityScopeKind.Exact), [], now.AddMinutes(-1), now.AddHours(1))])], [], now.AddHours(1));
        Assert.Equal(AuthorityMutationStatus.Applied, (await store.PublishAsync(new("publish-portable", _actor, snapshot, 0))).Status);
        var record = new AuthorityDecisionRecord(command.DecisionCommandId, command.Request,
            new(AuthorityStatus.Permit, "fixture.permit", snapshot.Version, "explicit-trusted-fixture", snapshot.Identity), now, "fixture-v1", "{}");
        Assert.Equal(AuthorityEvidenceStatus.Recorded, (await store.RecordDecisionAsync(_actor, record)).Status);
        var gate = new SqliteAuthorityOperationStartGate(store, journal);
        Assert.Equal(AuthorityOperationStartStatus.Started, (await gate.StartAsync(command)).Status);
        Assert.Equal(PatchRecoveryState.Started, (await journal.InspectAsync(_actor, _context, fixture.Record.OperationId, fixture.AdmissionIdentity)).State);
        Assert.Equal(AuthorityOperationStartStatus.AlreadyStarted, (await gate.StartAsync(command)).Status);
    }

    // Explicit infrastructure fixture. This is not a production authenticator
    // or a filesystem dispatch; real locked-writer tests exercise the full host.
    private sealed class CoreFixturePolicy(AuthorityStoreActor expected) : IAuthorityStoreAuthorizer
    {
        public ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(AuthorityStoreAccessRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(request.Actor == expected ? new AuthorityStoreAuthorization(AuthorityStatus.Permit, expected) : new AuthorityStoreAuthorization(AuthorityStatus.Deny));
    }

    [Fact]
    public async Task ApprovalReplayDoesNotRenewExpiredOrRevokedApproval()
    {
        var fixture = await BuildFixture(_actor, _context, "op-approve");
        var command = new PatchApprovalCommand("approve", _actor, _context, fixture.Admission, _clock.GetUtcNow().AddMinutes(2));
        var journal = Journal();

        Assert.Equal(PatchApprovalStatus.Recorded, (await journal.ApproveAsync(command)).Status);
        Assert.Equal(PatchApprovalStatus.Replayed, (await Journal().ApproveAsync(command)).Status);
        _clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(PatchApprovalStatus.Replayed, (await Journal().ApproveAsync(command)).Status);
        Assert.Equal(PatchApprovalStatus.Expired,
            (await Journal().ReadApprovalAsync(_actor, _context, command.Admission.OperationId, fixture.AdmissionIdentity)).Status);

        Assert.Equal(PatchApprovalStatus.Revoked,
            (await Journal().RevokeApprovalAsync(_actor, _context, command.Admission.OperationId,
                fixture.AdmissionIdentity, "revoke-1")).Status);
        Assert.Equal(PatchApprovalStatus.Replayed, (await Journal().ApproveAsync(command)).Status);
        Assert.Equal(PatchApprovalStatus.Revoked,
            (await Journal().ReadApprovalAsync(_actor, _context, command.Admission.OperationId, fixture.AdmissionIdentity)).Status);
        Assert.Equal(PatchApprovalStatus.Conflict,
            (await Journal().ApproveAsync(command with { CommandId = "different-command" })).Status);
    }

    [Fact]
    public async Task EveryReadAndReplayRequiresFreshMatchingAuthenticatedActor()
    {
        var fixture = await BuildFixture(_actor, _context, "op-auth");
        var command = new PatchApprovalCommand("approve", _actor, _context, fixture.Admission, _clock.GetUtcNow().AddMinutes(5));
        Assert.Equal(PatchApprovalStatus.Recorded, (await Journal().ApproveAsync(command)).Status);

        _authorizer.Status = AuthorityStatus.Deny;
        var denied = await Journal().ReadApprovalAsync(_actor, _context, command.Admission.OperationId, fixture.AdmissionIdentity);
        Assert.Equal(PatchApprovalStatus.Denied, denied.Status);
        Assert.Null(denied.Record);
        _authorizer.Status = AuthorityStatus.Permit;
        _authorizer.ReturnActor = _actor with { ActorId = "different" };
        var mismatched = await Journal().ApproveAsync(command);
        Assert.Equal(PatchApprovalStatus.Unavailable, mismatched.Status);
        Assert.Null(mismatched.Record);
    }

    [Fact]
    public async Task ApprovalKeysAreTenantAndOperationScoped()
    {
        var fixture = await BuildFixture(_actor, _context, "shared-op");
        var command = new PatchApprovalCommand("approve", _actor, _context, fixture.Admission, _clock.GetUtcNow().AddMinutes(5));
        Assert.Equal(PatchApprovalStatus.Recorded, (await Journal().ApproveAsync(command)).Status);

        var otherActor = new AuthorityStoreActor("other-tenant", "host", "session");
        var otherContext = new AuthenticatedAuthorityContext("other-tenant", "subject", "run", "revision", "fence");
        var other = await BuildFixture(otherActor, otherContext, "shared-op");
        Assert.Equal(PatchApprovalStatus.NotFound,
            (await Journal().ReadApprovalAsync(otherActor, otherContext, "shared-op", other.AdmissionIdentity)).Status);
    }

    [Fact]
    public async Task StartUsesSuppliedTransactionRollsBackAndCannotStartTwice()
    {
        var fixture = await Reserve("op-start");
        var journal = Journal();
        var command = PatchIdentity.Command(fixture.Record);

        await using (var connection = await journal.OpenAsync())
        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            var start = await journal.TryStartAsync(connection, transaction, command);
            Assert.Equal(AuthorityStatus.Permit, start.Status);
            Assert.Equal(fixture.Approval.ExpiresAt, start.ValidUntil);
            transaction.Rollback();
        }
        Assert.Equal(PatchRecoveryState.Reserved,
            (await Journal().InspectAsync(_actor, _context, "op-start", fixture.AdmissionIdentity)).State);

        await using (var connection = await journal.OpenAsync())
        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            var start = await journal.TryStartAsync(connection, transaction, command);
            Assert.Equal(AuthorityStatus.Permit, start.Status);
            transaction.Commit();
        }
        await using (var connection = await journal.OpenAsync())
        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            var duplicate = await journal.TryStartAsync(connection, transaction, command);
            Assert.Equal(AuthorityStatus.Deny, duplicate.Status);
            transaction.Rollback();
        }
        Assert.Equal(PatchRecoveryState.Started,
            (await Journal().InspectAsync(_actor, _context, "op-start", fixture.AdmissionIdentity)).State);
    }

    [Fact]
    public async Task ParticipantRejectsForeignAttachedAndTempShadowDatabasesButAllowsEmptyTempSchema()
    {
        var fixture = await Reserve("op-db-identity");
        var command = PatchIdentity.Command(fixture.Record);
        var otherPath = Path.Combine(Path.GetTempPath(), "hufu-patch-other-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var otherJournal = new SqlitePatchOutcomeJournal(otherPath, _authorizer, _clock);
            await using (var foreign = await otherJournal.OpenAsync())
            using (var transaction = foreign.BeginTransaction(deferred: false))
            {
                Assert.Equal(AuthorityStatus.Deny,
                    (await Journal().TryStartAsync(foreign, transaction, command)).Status);
                transaction.Rollback();
            }

            await using (var attached = await Journal().OpenAsync())
            {
                using (var attach = attached.CreateCommand())
                {
                    attach.CommandText = "ATTACH DATABASE $path AS other";
                    attach.Parameters.AddWithValue("$path", otherPath);
                    await attach.ExecuteNonQueryAsync();
                }
                using var transaction = attached.BeginTransaction(deferred: false);
                Assert.Equal(AuthorityStatus.Deny,
                    (await Journal().TryStartAsync(attached, transaction, command)).Status);
                transaction.Rollback();
            }

            await using (var shadowed = await Journal().OpenAsync())
            {
                using (var create = shadowed.CreateCommand())
                {
                    create.CommandText = "CREATE TEMP TABLE hp_outcomes(tenant_id TEXT, operation_id TEXT)";
                    await create.ExecuteNonQueryAsync();
                }
                using var transaction = shadowed.BeginTransaction(deferred: false);
                Assert.Equal(AuthorityStatus.Deny,
                    (await Journal().TryStartAsync(shadowed, transaction, command)).Status);
                transaction.Rollback();
            }
            Assert.Equal(PatchRecoveryState.Reserved,
                (await Journal().InspectAsync(_actor, _context, fixture.Record.OperationId, fixture.AdmissionIdentity)).State);
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
                if (File.Exists(otherPath + suffix)) File.Delete(otherPath + suffix);
        }
    }

    [Fact]
    public async Task RevocationBeforeStartBlocksParticipantAndCannotBeUndoneByApprovalReplay()
    {
        var fixture = await Reserve("op-revoked");
        Assert.Equal(PatchApprovalStatus.Revoked,
            (await Journal().RevokeApprovalAsync(_actor, _context, "op-revoked", fixture.AdmissionIdentity, "revoke")).Status);
        Assert.Equal(PatchApprovalStatus.Replayed, (await Journal().ApproveAsync(fixture.Approval)).Status);

        await using var connection = await Journal().OpenAsync();
        using var transaction = connection.BeginTransaction(deferred: false);
        var result = await Journal().TryStartAsync(connection, transaction, PatchIdentity.Command(fixture.Record));
        Assert.Equal(AuthorityStatus.Deny, result.Status);
        transaction.Rollback();
        Assert.Equal(PatchRecoveryState.Reserved,
            (await Journal().InspectAsync(_actor, _context, "op-revoked", fixture.AdmissionIdentity)).State);
    }

    [Fact]
    public async Task AmbiguousReconciliationClosesReservedAndStartedWithoutAuthorizingDispatch()
    {
        var fixture = await Reserve("op-reconcile");
        Assert.True(await Journal().ReconcileAmbiguousAsync(_actor, _context, "op-reconcile", fixture.AdmissionIdentity));
        var read = await Journal().InspectAsync(_actor, _context, "op-reconcile", fixture.AdmissionIdentity);
        Assert.Equal(PatchRecoveryState.Ambiguous, read.State);
        Assert.Equal(PatchIdentity.Evidence(fixture.Record), read.Record!.EvidenceId);
        Assert.True(await Journal().ReconcileAmbiguousAsync(_actor, _context, "op-reconcile", fixture.AdmissionIdentity));

        await using var connection = await Journal().OpenAsync();
        using var transaction = connection.BeginTransaction(deferred: false);
        var start = await Journal().TryStartAsync(connection, transaction, PatchIdentity.Command(fixture.Record));
        Assert.Equal(AuthorityStatus.Deny, start.Status);
        transaction.Rollback();

        var startedFixture = await Reserve("op-reconcile-started");
        await using (var startedConnection = await Journal().OpenAsync())
        using (var startedTransaction = startedConnection.BeginTransaction(deferred: false))
        {
            Assert.Equal(AuthorityStatus.Permit,
                (await Journal().TryStartAsync(startedConnection, startedTransaction,
                    PatchIdentity.Command(startedFixture.Record))).Status);
            startedTransaction.Commit();
        }
        Assert.True(await Journal().ReconcileAmbiguousAsync(_actor, _context,
            "op-reconcile-started", startedFixture.AdmissionIdentity));
        Assert.Equal(PatchRecoveryState.Ambiguous,
            (await Journal().InspectAsync(_actor, _context, "op-reconcile-started", startedFixture.AdmissionIdentity)).State);
    }

    [Theory]
    [InlineData(MutationOutcome.Completed, PatchRecoveryState.Completed)]
    [InlineData(MutationOutcome.NoMutation, PatchRecoveryState.NoMutation)]
    [InlineData(MutationOutcome.Ambiguous, PatchRecoveryState.Ambiguous)]
    public async Task ExactTerminalOutcomeIsDurableAndIdempotentButCannotBeRewritten(
        MutationOutcome outcome, PatchRecoveryState expectedState)
    {
        var fixture = await Reserve("op-terminal");
        var journal = Journal();
        await using (var connection = await journal.OpenAsync())
        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            Assert.Equal(AuthorityStatus.Permit,
                (await journal.TryStartAsync(connection, transaction, PatchIdentity.Command(fixture.Record))).Status);
            transaction.Commit();
        }
        var started = fixture.Record with { State = PatchRecoveryState.Started, EvidenceId = PatchIdentity.Evidence(fixture.Record) };
        var observed = outcome switch
        {
            MutationOutcome.Completed => started.Start.ProposedVersion,
            MutationOutcome.NoMutation => started.Start.OriginalVersion,
            _ => (ResourceVersion?)null
        };
        var completion = new MutationCompletion(started.Start, started.EvidenceId!, outcome, observed);

        Assert.True(await Journal().CompleteAsync(started, completion));
        Assert.True(await Journal().CompleteAsync(started, completion));
        Assert.Equal(expectedState,
            (await Journal().InspectAsync(_actor, _context, "op-terminal", fixture.AdmissionIdentity)).State);
        var changed = completion with { Outcome = outcome == MutationOutcome.Completed ? MutationOutcome.Ambiguous : MutationOutcome.Completed,
            ObservedVersion = outcome == MutationOutcome.Completed ? null : started.Start.ProposedVersion };
        Assert.False(await Journal().CompleteAsync(started, changed));
    }

    [Fact]
    public async Task CapacityReservesTerminalGrowthBeforeOutcomeCanBeInserted()
    {
        var fixture = await BuildFixture(_actor, _context, "op-capacity");
        var approval = new PatchApprovalCommand("approve", _actor, _context, fixture.Admission, _clock.GetUtcNow().AddMinutes(5));
        var journal = Journal(new PatchJournalOptions(MaxEntries: 1, MaxStoredBytes: PatchIdentity.MaxRecordBytes));
        Assert.Equal(PatchApprovalStatus.Recorded, (await journal.ApproveAsync(approval)).Status);
        var record = MakeRecord(fixture, _actor, _context);
        Assert.False(await journal.ReserveAsync(record));
        Assert.Equal(PatchRecoveryState.NotFound,
            (await journal.InspectAsync(_actor, _context, record.OperationId, record.AdmissionIdentity)).State);
    }

    [Fact]
    public async Task ApprovedAdmissionCannotBePairedWithAReboundMutationTarget()
    {
        var fixture = await BuildFixture(_actor, _context, "op-rebound");
        var approval = new PatchApprovalCommand("approve", _actor, _context, fixture.Admission, _clock.GetUtcNow().AddMinutes(5));
        Assert.Equal(PatchApprovalStatus.Recorded, (await Journal().ApproveAsync(approval)).Status);

        var record = MakeRecord(fixture, _actor, _context);
        var rebound = record with { Start = record.Start with { Path = new WorkspacePath("other.txt") } };
        rebound = rebound with { BindingJson = PatchIdentity.Binding(rebound) };
        rebound = rebound with { BindingIdentity = PatchIdentity.Hash(rebound.BindingJson) };
        Assert.True(PatchIdentity.ValidRecord(rebound));
        Assert.False(await Journal().ReserveAsync(rebound));
        Assert.Equal(PatchRecoveryState.NotFound,
            (await Journal().InspectAsync(_actor, _context, record.OperationId, record.AdmissionIdentity)).State);
    }

    [Fact]
    public async Task CorruptJournalBodyFailsClosedOnRead()
    {
        var fixture = await Reserve("op-corrupt");
        var started = fixture.Record with { State = PatchRecoveryState.Started, EvidenceId = PatchIdentity.Evidence(fixture.Record) };
        await using (var startConnection = await Journal().OpenAsync())
        using (var transaction = startConnection.BeginTransaction(deferred: false))
        {
            Assert.Equal(AuthorityStatus.Permit,
                (await Journal().TryStartAsync(startConnection, transaction, PatchIdentity.Command(fixture.Record))).Status);
            transaction.Commit();
        }
        Assert.True(await Journal().CompleteAsync(started,
            new MutationCompletion(started.Start, started.EvidenceId!, MutationOutcome.Completed, started.Start.ProposedVersion)));
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE hp_outcomes SET body=$body";
            command.Parameters.AddWithValue("$body", new byte[] { 0x7b, 0x7d });
            await command.ExecuteNonQueryAsync();
        }
        Assert.Equal(PatchRecoveryState.Unavailable,
            (await Journal().InspectAsync(_actor, _context, fixture.Record.OperationId, fixture.AdmissionIdentity)).State);
    }

    [Fact]
    public async Task OpenRejectsAnUnrecognizedApplicationSchema()
    {
        var path = Path.Combine(Path.GetTempPath(), "hufu-patch-foreign-schema-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE foreign_state(id INTEGER PRIMARY KEY)";
                await command.ExecuteNonQueryAsync();
            }
            var journal = new SqlitePatchOutcomeJournal(path, _authorizer, _clock);
            await Assert.ThrowsAsync<InvalidDataException>(async () => await journal.OpenAsync());
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    private async Task<ReservedFixture> Reserve(string operationId)
    {
        var fixture = await BuildFixture(_actor, _context, operationId);
        var approval = new PatchApprovalCommand("approve-" + operationId, _actor, _context,
            fixture.Admission, _clock.GetUtcNow().AddMinutes(5));
        Assert.Equal(PatchApprovalStatus.Recorded, (await Journal().ApproveAsync(approval)).Status);
        var record = MakeRecord(fixture, _actor, _context);
        Assert.True(await Journal().ReserveAsync(record));
        return new(fixture.Admission, fixture.AdmissionIdentity, approval, record);
    }

    private static PatchOutcomeRecord MakeRecord(AdmissionFixture fixture, AuthorityStoreActor actor,
        AuthenticatedAuthorityContext context)
    {
        var proposal = fixture.Admission.Plan.Nodes[0].Proposals[0];
        var mutation = new MutationStartRequest(fixture.Admission.Invocation, fixture.Admission.ResourceRequestIdentity,
            fixture.Admission.Plan.Workspace, new(proposal.RelativePath), PatchIdentity.WriterProfile, "ntfs:volume:fileid",
            proposal.OriginalVersion, new(PatchIdentity.VersionPrefix + proposal.ProposedSha256),
            proposal.OriginalByteLength, proposal.ProposedByteLength);
        var admissionIdentity = PatchIdentity.Admission(context, fixture.Admission);
        var draft = new PatchOutcomeRecord(actor, context, fixture.Admission.OperationId, admissionIdentity,
            fixture.Admission.Document.Identity, fixture.Admission.Document.Nodes[0].Identity, fixture.Admission.Plan.Identity,
            mutation, "decision-" + fixture.Admission.OperationId, 1, "", "", PatchRecoveryState.Reserved);
        var binding = PatchIdentity.Binding(draft);
        return draft with { BindingJson = binding, BindingIdentity = PatchIdentity.Hash(binding) };
    }

    private async Task<AdmissionFixture> BuildFixture(AuthorityStoreActor actor,
        AuthenticatedAuthorityContext context, string operationId)
    {
        var workspace = new WorkspaceId("workspace");
        var provider = new MemoryProvider(workspace, "old"u8.ToArray());
        var document = PreviewCompiler.Compile([new FilePatchStage("note.txt",
            [new TextPatch(0, 3, "new"u8.ToArray())])], workspace).Document!;
        var preview = await new PreviewRuntime(new WorkspaceReference(workspace.Value), provider, new PreviewPermit())
            .WhatIfAsync(new EffectInvocation(context.SubjectId, "effect", "attempt"), document);
        Assert.Equal(PreviewRunStatus.Succeeded, preview.Status);
        var admission = HufuSinglePatchHost.PrepareAdmission(context, document, preview.Plan!, operationId);
        var identity = PatchIdentity.Admission(context, admission);
        return new(admission, identity);
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            if (File.Exists(_path + suffix)) File.Delete(_path + suffix);
    }

    private sealed record AdmissionFixture(PatchAdmissionRequest Admission, string AdmissionIdentity);
    private sealed record ReservedFixture(PatchAdmissionRequest Admission, string AdmissionIdentity,
        PatchApprovalCommand Approval, PatchOutcomeRecord Record);

    private sealed class RecordingAuthorizer : IPatchJournalAuthorizer
    {
        public AuthorityStatus Status { get; set; } = AuthorityStatus.Permit;
        public AuthorityStoreActor? ReturnActor { get; set; }
        public List<PatchJournalAccessRequest> Requests { get; } = [];

        public ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(PatchJournalAccessRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(new AuthorityStoreAuthorization(Status,
                Status == AuthorityStatus.Permit ? ReturnActor ?? request.Actor : null));
        }
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }

    private sealed class PreviewPermit : IPreviewAuthorizer
    {
        public ValueTask<LanguageAuthorityDecision> AuthorizeAsync(PreviewAuthorizationRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new LanguageAuthorityDecision(LanguageAuthorityStatus.Permit));
    }

    private sealed class MemoryProvider(WorkspaceId workspace, byte[] content) : IWorkspaceProvider
    {
        public WorkspaceId Workspace { get; } = workspace;
        public WorkspaceProviderCapabilities Capabilities { get; } =
            new(PreviewProfile.ProviderProfile, PatchIdentity.WriterProfile, SupportsReads: true, SupportsConditionalWrites: true);
        public IWorkspaceReaderSession OpenReader(IResourceAuthorizer authorizer, WorkspaceReaderOptions? options = null) => new MemoryReader(content);
        public IWorkspaceConditionalWriter OpenWriter(IResourceAuthorizer authorizer, IResourceMutationJournal journal,
            WorkspaceWriterOptions? options = null) => throw new NotSupportedException();
    }

    private sealed class MemoryReader(byte[] content) : IWorkspaceReaderSession
    {
        public ValueTask<ResourceResult<FileReadResult>> ReadFileAsync(FileReadRequest request,
            CancellationToken cancellationToken = default)
        {
            var data = (byte[])content.Clone();
            var version = new ResourceVersion(PatchIdentity.VersionPrefix + Convert.ToHexString(SHA256.HashData(data)));
            return ValueTask.FromResult(ResourceResult<FileReadResult>.Success(new(data, version)));
        }
        public ValueTask<ResourceResult<FileMetadata>> GetFileMetadataAsync(FileMetadataRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ResourceResult<FileMetadata>.Success(new(false, null, null)));
        public ValueTask<ResourceResult<DirectoryPage>> ListDirectoryAsync(DirectoryListRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ResourceResult<DirectoryPage>.Success(new([], true, null, null)));
        public void Dispose() { }
    }
}
