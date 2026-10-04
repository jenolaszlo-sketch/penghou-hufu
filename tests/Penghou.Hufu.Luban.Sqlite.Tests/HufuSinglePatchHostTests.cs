using System.Text;
using System.Text.Json;
using Penghou.Hufu;
using Penghou.Hufu.Cedar;
using Penghou.Hufu.Luban.Sqlite;
using Penghou.Hufu.Sqlite;
using Penghou.IO.Abstractions;
using Penghou.IO.Local;
using Penghou.Luban;
using Penghou.Luban.Changes;
using Penghou.Luban.Execution;
using Penghou.Luban.Language;
using Penghou.Luban.Resolution;
using Xunit;

namespace Penghou.Hufu.Luban.Sqlite.Tests;

// This suite qualifies the Windows HostControlled provider profile. The fixture
// uses the real host, SQLite store/journal, Cedar evaluator, and local writer.
public sealed class HufuSinglePatchHostTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ActualProviderWriteIsCompletedDurablyAcrossReopenAndNeverDispatchesAgain()
    {
        using var f = new Fixture();
        var run = await f.PrepareAsync();

        var first = await f.ExecuteAsync(run);

        Assert.True(first.Succeeded, first.Failure?.ToString());
        Assert.Equal("new", f.ReadTarget());
        Assert.Equal(PatchRecoveryState.Completed, (await f.Host.InspectAsync(run.Admission)).State);
        Assert.Equal(1, f.Provider.WriterOpens);

        var reopened = f.ReopenHost();
        var replay = await f.ExecuteAsync(run, reopened);

        Assert.False(replay.Succeeded);
        Assert.Equal("new", f.ReadTarget());
        Assert.Equal(1, f.Provider.WriterOpens);
        Assert.Equal(PatchRecoveryState.Completed, (await reopened.InspectAsync(run.Admission)).State);
    }

    [Fact]
    public async Task LostStartResponseLeavesStartedReceiptAndDoesNotWriteOrRedispatch()
    {
        using var f = new Fixture();
        var run = await f.PrepareAsync();
        var lost = new ResponseLossHost(f.Host) { LoseStartResponse = true };

        var first = await f.ExecuteAsync(run, lost);
        var state = await f.Host.InspectAsync(run.Admission);
        var retry = await f.ExecuteAsync(run);

        Assert.False(first.Succeeded);
        Assert.Equal("old", f.ReadTarget());
        Assert.Equal(PatchRecoveryState.Started, state.State);
        Assert.False(retry.Succeeded);
        Assert.Equal(1, f.Provider.WriterOpens);
        Assert.Equal(1, lost.StartCalls);
    }

    [Fact]
    public async Task LostCompletionResponseReturnsAmbiguousAlthoughCompletedReceiptSurvives()
    {
        using var f = new Fixture();
        var run = await f.PrepareAsync();
        var lost = new ResponseLossHost(f.Host) { LoseCompletionResponse = true };

        var first = await f.ExecuteAsync(run, lost);
        var state = await f.Host.InspectAsync(run.Admission);
        var retry = await f.ExecuteAsync(run);

        Assert.Equal(ResourceFailureKind.AmbiguousOutcome, first.Failure);
        Assert.Equal("new", f.ReadTarget());
        Assert.Equal(PatchRecoveryState.Completed, state.State);
        Assert.False(retry.Succeeded);
        Assert.Equal(1, f.Provider.WriterOpens);
        Assert.Equal(1, lost.StartCalls);
    }

    [Theory]
    [InlineData("mismatch")]
    [InlineData("expired")]
    [InlineData("revoked")]
    public async Task MissingExactActiveApprovalPreventsProviderWriter(string approvalState)
    {
        using var f = new Fixture();
        var run = await f.CaptureAsync("op-approval");
        if (approvalState == "mismatch")
        {
            var other = await f.CaptureAsync("op-approval", "other.txt");
            Assert.Equal(PatchApprovalStatus.Recorded, (await f.ApproveAsync(other, "approve-other")).Status);
        }
        else
        {
            Assert.Equal(PatchApprovalStatus.Recorded,
                (await f.ApproveAsync(run, "approve-exact", approvalState == "expired" ? TimeSpan.FromMinutes(1) : TimeSpan.FromHours(1))).Status);
            if (approvalState == "expired") f.Clock.Advance(TimeSpan.FromMinutes(2));
            if (approvalState == "revoked")
                Assert.Equal(PatchApprovalStatus.Revoked,
                    (await f.Journal.RevokeApprovalAsync(f.Actor, f.Context, run.Admission.OperationId,
                        HufuSinglePatchHost.AdmissionIdentity(f.Context, run.Admission), "revoke-exact")).Status);
        }
        var result = await f.ExecuteAsync(run);

        Assert.False(result.Succeeded);
        Assert.Equal("old", f.ReadTarget());
        Assert.Equal(0, f.Provider.WriterOpens);
        Assert.Equal(PatchRecoveryState.NotFound, (await f.Host.InspectAsync(run.Admission)).State);
    }

    [Theory]
    [InlineData(AuthorityAction.ReadFile)]
    [InlineData(AuthorityAction.ReadMetadata)]
    [InlineData(AuthorityAction.PatchFile)]
    [InlineData(AuthorityAction.WriteFile)]
    [InlineData(AuthorityAction.Release)]
    public async Task ExcludingAnyRequiredActionPreventsPhysicalMutation(AuthorityAction excluded)
    {
        using var f = new Fixture();
        var run = await f.PrepareAsync();
        Assert.Equal(AuthorityMutationStatus.Applied,
            (await f.PublishAsync(f.SnapshotExcept(excluded), expectedSequence: 1, commandId: "exclude-" + excluded)).Status);

        var result = await f.ExecuteAsync(run);

        Assert.False(result.Succeeded);
        Assert.Equal("old", f.ReadTarget());
        Assert.Equal(0, f.Provider.WriterOpens);
        Assert.Equal(PatchRecoveryState.NotFound, (await f.Host.InspectAsync(run.Admission)).State);
    }

    [Fact]
    public async Task ApprovalRevocationAtReserveWinsBeforeStartAndWrite()
    {
        using var f = new Fixture();
        var run = await f.PrepareAsync();
        f.JournalPolicy.BeforeAuthorize = async request =>
        {
            if (request.Operation == PatchJournalOperation.Reserve)
            {
                f.JournalPolicy.BeforeAuthorize = null;
                await f.Journal.RevokeApprovalAsync(f.Actor, f.Context, run.Admission.OperationId,
                    HufuSinglePatchHost.AdmissionIdentity(f.Context, run.Admission), "revoke-at-reserve");
            }
        };

        var result = await f.ExecuteAsync(run);

        Assert.False(result.Succeeded);
        Assert.Equal("old", f.ReadTarget());
        Assert.Equal(1, f.Provider.WriterOpens);
        Assert.Equal(PatchRecoveryState.NotFound, (await f.Host.InspectAsync(run.Admission)).State);
    }

    [Fact]
    public async Task AuthorityRevocationBeforeStartWriterOrderBlocksWrite()
    {
        using var f = new Fixture();
        var run = await f.PrepareAsync();
        f.StorePolicy.BeforeAuthorize = async request =>
        {
            if (request.Operation == AuthorityStoreOperation.StartOperation)
            {
                f.StorePolicy.BeforeAuthorize = null;
                await f.Store.RevokeAsync(new("revoke-before-start", f.Actor, f.Context, 1, "test-revoked"));
            }
        };

        var result = await f.ExecuteAsync(run);

        Assert.False(result.Succeeded);
        Assert.Equal("old", f.ReadTarget());
        Assert.Equal(1, f.Provider.WriterOpens);
        Assert.Equal(PatchRecoveryState.Reserved, (await f.Host.InspectAsync(run.Admission)).State);
    }

    [Fact]
    public async Task SnapshotChangeDuringFinalStartChecksRejectsMixedDecisionEvidence()
    {
        using var f = new Fixture();
        var run = await f.PrepareAsync();
        f.StorePolicy.BeforeAuthorize = async request =>
        {
            if (request.Operation == AuthorityStoreOperation.RecordDecision &&
                request.DecisionRecord?.Request.Action == AuthorityAction.PatchFile &&
                request.DecisionRecord.Request.RequestIdentity == run.Admission.ResourceRequestIdentity.Value)
            {
                f.StorePolicy.BeforeAuthorize = null;
                await f.PublishAsync(f.SnapshotExcept(AuthorityAction.WriteFile), 1, "publish-mid-start");
            }
        };

        var result = await f.ExecuteAsync(run);

        Assert.False(result.Succeeded);
        Assert.Equal("old", f.ReadTarget());
        Assert.Equal(PatchRecoveryState.NotFound, (await f.Host.InspectAsync(run.Admission)).State);
        Assert.Equal(1, f.Provider.WriterOpens);
    }

    [Fact]
    public async Task PatchProofAnchorsWholeSnapshotValidityThroughOperationStart()
    {
        using var f = new Fixture();
        var run = await f.PrepareAsync();
        Assert.Equal(AuthorityMutationStatus.Applied,
            (await f.PublishAsync(f.SnapshotWithShortReadGrant(), 1, "publish-short-read-grant")).Status);
        var inFinalChecks = false;
        var advanceAfterFinalReadProof = false;
        f.StorePolicy.BeforeAuthorize = request =>
        {
            if (!inFinalChecks) return Task.CompletedTask;
            if (request.Operation == AuthorityStoreOperation.RecordDecision &&
                request.DecisionRecord?.Request.Action == AuthorityAction.ReadFile)
            {
                advanceAfterFinalReadProof = true;
            }
            else if (request.Operation == AuthorityStoreOperation.ReadCurrent && advanceAfterFinalReadProof)
            {
                advanceAfterFinalReadProof = false;
                f.Clock.Advance(TimeSpan.FromMinutes(2));
            }
            return Task.CompletedTask;
        };
        var turnOnFinalChecks = new CallbackHost(f.Host)
        {
            BeforeStart = _ => { inFinalChecks = true; return Task.CompletedTask; }
        };

        var result = await f.ExecuteAsync(run, turnOnFinalChecks);

        Assert.False(result.Succeeded);
        Assert.Equal("old", f.ReadTarget());
        Assert.Equal(PatchRecoveryState.Reserved, (await f.Host.InspectAsync(run.Admission)).State);
        Assert.Equal(1, f.Provider.WriterOpens);
    }

    [Fact]
    public async Task StartCommandWithMismatchedFreshActorCannotCommitOrWrite()
    {
        using var f = new Fixture();
        var run = await f.PrepareAsync();
        f.StorePolicy.WrongActorOnStart = true;

        var result = await f.ExecuteAsync(run);

        Assert.False(result.Succeeded);
        Assert.Equal("old", f.ReadTarget());
        Assert.Equal(PatchRecoveryState.Reserved, (await f.Host.InspectAsync(run.Admission)).State);
        Assert.Equal(1, f.Provider.WriterOpens);
    }

    [Fact]
    public async Task ReleaseDeniedAfterCommittedStartKeepsCompletedOutcomeAndWithholdsReceipt()
    {
        using var f = new Fixture();
        var run = await f.PrepareAsync();
        var revokeAfterStart = new CallbackHost(f.Host)
        {
            AfterStart = async decision =>
            {
                if (decision.Status == MutationStartStatus.Started)
                    await f.PublishAsync(f.SnapshotExcept(AuthorityAction.Release), 1, "remove-release-after-start");
            }
        };

        var result = await f.ExecuteAsync(run, revokeAfterStart);

        Assert.Equal(ResourceFailureKind.AmbiguousOutcome, result.Failure);
        Assert.Equal("new", f.ReadTarget());
        Assert.Equal(PatchRecoveryState.Completed, (await f.Host.InspectAsync(run.Admission)).State);
        Assert.Equal(1, f.Provider.WriterOpens);
    }

    [Fact]
    public async Task RequiredEvidenceFailureBeforeStartLeavesFileAndJournalUntouched()
    {
        using var f = new Fixture();
        var run = await f.PrepareAsync();
        f.Evidence.ReturnNull = true;

        var result = await f.ExecuteAsync(run);

        Assert.False(result.Succeeded);
        Assert.Equal("old", f.ReadTarget());
        Assert.Equal(0, f.Provider.WriterOpens);
        Assert.Equal(PatchRecoveryState.NotFound, (await f.Host.InspectAsync(run.Admission)).State);
    }

    [Fact]
    public async Task CancellationImmediatelyAfterStartRecordsNoMutationBeforePropagation()
    {
        using var f = new Fixture();
        var run = await f.PrepareAsync();
        using var cancellation = new CancellationTokenSource();
        var cancel = new CallbackHost(f.Host) { AfterStart = _ => { cancellation.Cancel(); return Task.CompletedTask; } };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await f.ExecuteAsync(run, cancel, cancellation.Token));

        Assert.Equal("old", f.ReadTarget());
        Assert.Equal(PatchRecoveryState.NoMutation, (await f.Host.InspectAsync(run.Admission)).State);
        Assert.Equal(1, f.Provider.WriterOpens);
    }

    [Fact]
    public async Task ChangedFilePreconditionFailsBeforeStartAndPreservesExternalBytes()
    {
        using var f = new Fixture();
        var run = await f.PrepareAsync();
        f.WriteTarget("OLD");

        var result = await f.ExecuteAsync(run);

        Assert.Equal(ResourceFailureKind.PreconditionFailed, result.Failure);
        Assert.Equal("OLD", f.ReadTarget());
        Assert.Equal(PatchRecoveryState.NotFound, (await f.Host.InspectAsync(run.Admission)).State);
    }

    [Theory]
    [InlineData("object")]
    [InlineData("profile")]
    [InlineData("before-version")]
    [InlineData("after-version")]
    [InlineData("fence")]
    public async Task AlteredStartFactsAreRejectedBeforePhysicalWrite(string tamper)
    {
        using var f = new Fixture();
        var run = await f.PrepareAsync();
        var wrapper = new TamperedStartHost(f.Host, mutation => tamper switch
        {
            "object" => mutation with { ObjectIdentity = "" },
            "profile" => mutation with { ProviderProfile = "other-profile" },
            "before-version" => mutation with { OriginalVersion = new ResourceVersion("local-read-v1:sha256:" + new string('a', 64)) },
            "after-version" => mutation with { ProposedVersion = new ResourceVersion("local-read-v1:sha256:" + new string('b', 64)) },
            "fence" => mutation with { Invocation = mutation.Invocation with { SubjectId = "other-subject" } },
            _ => mutation
        });

        var result = await f.ExecuteAsync(run, wrapper);

        Assert.False(result.Succeeded);
        Assert.Equal("old", f.ReadTarget());
        Assert.Equal(PatchRecoveryState.NotFound, (await f.Host.InspectAsync(run.Admission)).State);
    }

    private sealed class Fixture : IDisposable
    {
        private const string Target = "docs/target.txt";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "hufu-single-host-" + Guid.NewGuid().ToString("N"));
        private readonly string _database = Path.Combine(Path.GetTempPath(), "hufu-single-host-db-" + Guid.NewGuid().ToString("N") + ".db");
        public readonly WorkspaceId Workspace = new("hufu-single-host-workspace");
        public readonly AuthorityStoreActor Actor = new("tenant", "host", "session");
        public readonly AuthenticatedAuthorityContext Context = new("tenant", "subject", "run", "revision", "fence");
        public readonly TestClock Clock = new(Now);
        public readonly TestStoreAuthorizer StorePolicy;
        public readonly TestJournalAuthorizer JournalPolicy;
        public readonly TestEvidenceFactory Evidence = new();
        public readonly CountingProvider Provider;
        public SqlitePatchOutcomeJournal Journal { get; private set; } = null!;
        public SqliteAuthorityStore Store { get; private set; } = null!;
        public HufuSinglePatchHost Host { get; private set; } = null!;

        public Fixture()
        {
            Directory.CreateDirectory(Path.Combine(_root, "docs"));
            WriteTarget("old");
            StorePolicy = new(Actor);
            JournalPolicy = new(Actor);
            Provider = new CountingProvider(new LocalWorkspaceProvider(Workspace, _root, LocalPatchNamespace.HostControlled));
            ReopenHost();
        }

        public string ReadTarget() => File.ReadAllText(Path.Combine(_root, Target.Replace('/', Path.DirectorySeparatorChar)));
        public void WriteTarget(string text) => File.WriteAllText(Path.Combine(_root, Target.Replace('/', Path.DirectorySeparatorChar)), text, new UTF8Encoding(false));

        public async Task<Run> PrepareAsync(string operationId = "op-main")
        {
            var run = await CaptureAsync(operationId);
            var approval = await ApproveAsync(run, "approve-" + operationId);
            Assert.Equal(PatchApprovalStatus.Recorded, approval.Status);
            return run;
        }

        public async Task<Run> CaptureAsync(string operationId, string path = Target)
        {
            if (path != Target && !File.Exists(Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar))))
            {
                var otherPath = Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(otherPath)!);
                File.WriteAllText(otherPath, "old", new UTF8Encoding(false));
            }
            if ((await Store.ReadCurrentAsync(Actor, Context)).Status == AuthorityReadStatus.NotFound)
                await PublishInitialAsync();
            var compilation = PreviewCompiler.Compile(
                [new FilePatchStage(path, [new TextPatch(0, 3, Encoding.UTF8.GetBytes("new"))])], Workspace);
            Assert.NotNull(compilation.Document);
            var document = compilation.Document!;
            var invocation = new EffectInvocation(Context.SubjectId, "effect", "attempt-" + operationId);
            var capture = await new PreviewRuntime(new WorkspaceReference(Workspace.Value), Provider,
                Host.CreatePreviewAuthorizer(invocation, document)).WhatIfAsync(invocation, document);
            Assert.Equal(PreviewRunStatus.Succeeded, capture.Status);
            Assert.NotNull(capture.Plan);
            var admission = HufuSinglePatchHost.PrepareAdmission(Context, document, capture.Plan!, operationId);
            return new(document, capture.Plan!, admission);
        }

        public Task<PatchApprovalResult> ApproveAsync(Run run, string commandId, TimeSpan? duration = null) =>
            Journal.ApproveAsync(new PatchApprovalCommand(commandId, Actor, Context, run.Admission,
                Clock.GetUtcNow() + (duration ?? TimeSpan.FromHours(1)))).AsTask();

        public async Task<AuthorityMutationResult> PublishAsync(AuthoritySnapshot snapshot, long expectedSequence, string commandId) =>
            await Store.PublishAsync(new(commandId, Actor, snapshot, expectedSequence));

        public AuthoritySnapshot SnapshotExcept(AuthorityAction excluded)
        {
            var actions = AllActions.Where(action => action != excluded).ToArray();
            return Snapshot(actions);
        }

        public AuthoritySnapshot SnapshotWithShortReadGrant() =>
            new(Context, "snapshot-short-read-validity",
                [new AuthorityLayer("host", [
                    new AuthorityGrant("read-short", [AuthorityAction.ReadFile],
                        new(Workspace.Value, "", AuthorityScopeKind.Subtree), [], Now.AddHours(-1), Now.AddMinutes(1)),
                    new AuthorityGrant("write-and-disclose", [AuthorityAction.ReadMetadata, AuthorityAction.PatchFile,
                            AuthorityAction.WriteFile, AuthorityAction.Release],
                        new(Workspace.Value, "", AuthorityScopeKind.Subtree), [], Now.AddHours(-1), Now.AddDays(1))])], [], Now.AddDays(1));

        public async Task<AuthorityMutationResult> PublishInitialAsync()
        {
            var result = await PublishAsync(Snapshot(), 0, "publish-initial");
            Assert.Equal(AuthorityMutationStatus.Applied, result.Status);
            return result;
        }

        private AuthoritySnapshot Snapshot(IReadOnlyList<AuthorityAction>? actions = null) =>
            new(Context, "snapshot-" + Guid.NewGuid().ToString("N"),
                [new AuthorityLayer("host", [new AuthorityGrant("workspace", actions ?? AllActions,
                    new(Workspace.Value, "", AuthorityScopeKind.Subtree), [], Now.AddHours(-1), Now.AddDays(1))])], [], Now.AddDays(1));

        public async Task<PatchExecutionResult> ExecuteAsync(Run run, IPatchExecutionHost? host = null, CancellationToken cancellationToken = default)
        {
            return await new SinglePatchExecutor(new WorkspaceReference(Workspace.Value), Provider, host ?? Host)
                .ExecuteAsync(run.Document, run.Plan, run.Admission.OperationId, cancellationToken);
        }

        public HufuSinglePatchHost ReopenHost()
        {
            Journal = new SqlitePatchOutcomeJournal(_database, JournalPolicy, Clock);
            Store = new SqliteAuthorityStore(Journal, StorePolicy);
            Host = new HufuSinglePatchHost(Actor, Context, Store, new CedarAuthorityEvaluator(), Evidence, Journal);
            return Host;
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
                if (File.Exists(_database + suffix)) File.Delete(_database + suffix);
        }

        private static readonly AuthorityAction[] AllActions =
        [AuthorityAction.ReadFile, AuthorityAction.ReadMetadata, AuthorityAction.PatchFile,
            AuthorityAction.WriteFile, AuthorityAction.Release];
    }

    private sealed record Run(CompiledPreviewDocument Document, ResolvedEffectPlan Plan, PatchAdmissionRequest Admission);

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class TestStoreAuthorizer(AuthorityStoreActor expected) : IAuthorityStoreAuthorizer
    {
        public Func<AuthorityStoreAccessRequest, Task>? BeforeAuthorize { get; set; }
        public AuthorityStatus Status { get; set; } = AuthorityStatus.Permit;
        public bool WrongActorOnStart { get; set; }
        public async ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(AuthorityStoreAccessRequest request,
            CancellationToken cancellationToken = default)
        {
            if (BeforeAuthorize is { } callback) await callback(request);
            var matches = request.Actor == expected && request.Actor.TenantId == expected.TenantId;
            var permit = Status == AuthorityStatus.Permit && matches;
            var actor = permit ? WrongActorOnStart && request.Operation == AuthorityStoreOperation.StartOperation
                ? expected with { ActorId = "wrong-authenticated-actor" } : expected : null;
            return new(permit ? AuthorityStatus.Permit : Status == AuthorityStatus.Deny || !matches ? AuthorityStatus.Deny : AuthorityStatus.Unavailable,
                actor);
        }
    }

    private sealed class TestJournalAuthorizer(AuthorityStoreActor expected) : IPatchJournalAuthorizer
    {
        public Func<PatchJournalAccessRequest, Task>? BeforeAuthorize { get; set; }
        public AuthorityStatus Status { get; set; } = AuthorityStatus.Permit;
        public async ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(PatchJournalAccessRequest request,
            CancellationToken cancellationToken = default)
        {
            if (BeforeAuthorize is { } callback) await callback(request);
            var matches = request.Actor == expected && request.Actor.TenantId == expected.TenantId;
            return new(Status == AuthorityStatus.Permit && matches ? AuthorityStatus.Permit : Status == AuthorityStatus.Deny || !matches ? AuthorityStatus.Deny : AuthorityStatus.Unavailable,
                Status == AuthorityStatus.Permit && matches ? expected : null);
        }
    }

    private sealed class TestEvidenceFactory : IHufuPatchDecisionEvidenceFactory
    {
        private long _next;
        public bool ReturnNull { get; set; }
        public ValueTask<AuthorityDecisionRecord?> CreateAsync(AuthorityRequest request, AuthorityDecision decision,
            DateTimeOffset evaluatedAt, CancellationToken cancellationToken = default)
        {
            if (ReturnNull) return ValueTask.FromResult<AuthorityDecisionRecord?>(null);
            var commandId = "decision-" + Interlocked.Increment(ref _next).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var json = JsonSerializer.Serialize(new { request.Action, request.WorkspaceId, request.RelativePath, decision.SnapshotIdentity });
            return ValueTask.FromResult<AuthorityDecisionRecord?>(new(commandId, request, decision, evaluatedAt, "host-test-v1", json));
        }
    }

    private sealed class CountingProvider(LocalWorkspaceProvider inner) : IWorkspaceProvider
    {
        public int WriterOpens { get; private set; }
        public WorkspaceId Workspace => inner.Workspace;
        public WorkspaceProviderCapabilities Capabilities => inner.Capabilities;
        public IWorkspaceReaderSession OpenReader(IResourceAuthorizer authorizer, WorkspaceReaderOptions? options = null) =>
            inner.OpenReader(authorizer, options);
        public IWorkspaceConditionalWriter OpenWriter(IResourceAuthorizer authorizer, IResourceMutationJournal journal,
            WorkspaceWriterOptions? options = null)
        {
            WriterOpens++;
            return inner.OpenWriter(authorizer, journal, options);
        }
    }

    private class DelegatingHost(IPatchExecutionHost inner) : IPatchExecutionHost
    {
        public virtual ValueTask<LanguageAuthorityDecision> AdmitAsync(PatchAdmissionRequest request, CancellationToken cancellationToken = default) => inner.AdmitAsync(request, cancellationToken);
        public virtual ValueTask<ResourceAuthorizationDecision> AuthorizeResourceAsync(PatchResourceCheck request, CancellationToken cancellationToken = default) => inner.AuthorizeResourceAsync(request, cancellationToken);
        public virtual ValueTask<MutationStartDecision> StartAsync(PatchStartRequest request, CancellationToken cancellationToken = default) => inner.StartAsync(request, cancellationToken);
        public virtual ValueTask<bool> CompleteAsync(PatchCompletion completion, CancellationToken cancellationToken = default) => inner.CompleteAsync(completion, cancellationToken);
    }

    private sealed class ResponseLossHost(IPatchExecutionHost inner) : DelegatingHost(inner)
    {
        public bool LoseStartResponse { get; init; }
        public bool LoseCompletionResponse { get; init; }
        public int StartCalls { get; private set; }
        public override async ValueTask<MutationStartDecision> StartAsync(PatchStartRequest request, CancellationToken cancellationToken = default)
        {
            StartCalls++;
            var result = await base.StartAsync(request, cancellationToken);
            return LoseStartResponse && result.Status == MutationStartStatus.Started
                ? new(MutationStartStatus.Unavailable) : result;
        }
        public override async ValueTask<bool> CompleteAsync(PatchCompletion completion, CancellationToken cancellationToken = default)
        {
            var result = await base.CompleteAsync(completion, cancellationToken);
            return LoseCompletionResponse ? false : result;
        }
    }

    private sealed class CallbackHost(IPatchExecutionHost inner) : DelegatingHost(inner)
    {
        public Func<PatchStartRequest, Task>? BeforeStart { get; init; }
        public Func<MutationStartDecision, Task>? AfterStart { get; init; }
        public override async ValueTask<MutationStartDecision> StartAsync(PatchStartRequest request, CancellationToken cancellationToken = default)
        {
            if (BeforeStart is { } before) await before(request);
            var result = await base.StartAsync(request, cancellationToken);
            if (AfterStart is { } callback) await callback(result);
            return result;
        }
    }

    private sealed class TamperedStartHost(IPatchExecutionHost inner, Func<MutationStartRequest, MutationStartRequest> mutate)
        : DelegatingHost(inner)
    {
        public override ValueTask<MutationStartDecision> StartAsync(PatchStartRequest request, CancellationToken cancellationToken = default) =>
            base.StartAsync(request with { Mutation = mutate(request.Mutation) }, cancellationToken);
    }
}
