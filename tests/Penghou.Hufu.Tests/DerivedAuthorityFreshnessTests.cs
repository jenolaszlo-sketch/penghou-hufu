using Xunit;

namespace Penghou.Hufu.Tests;

/// <summary>
/// AE-1.3 optimistic freshness: after mandatory evidence recording, every
/// contributing head (child snapshot, current parent, lifecycle binding) is
/// re-read and must match the captured observation. Any movement fails closed
/// with typed <c>authority.read-set-superseded</c> and yields no usable Permit.
/// </summary>
public sealed class DerivedAuthorityFreshnessTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly AuthenticatedAuthorityContext Supervisor =
        new("tenant", "supervisor", "run-sup", "rev", "fence");
    private static readonly AuthenticatedAuthorityContext Child =
        new("tenant", "delegation", "run-child", "rev", "fence");
    private static readonly ExecutionLifecycleBinding LifecycleBinding = ExecutionLifecycleBinding.FromContext(Child);

    [Fact]
    public async Task UnchangedReadSet_Permits()
    {
        var fixture = new Fixture();
        var result = await fixture.Authorizer().AuthorizeAsync(fixture.Request("op"), CancellationToken.None);
        Assert.True(result.IsAuthorized);
        Assert.Equal(AuthorityStatus.Permit, result.Status);
    }

    [Fact]
    public async Task ChildHeadMovesDuringRecording_IsSuperseded()
    {
        var fixture = new Fixture();
        fixture.Recorder = new RecordingRecorder(1, () => fixture.MoveChild());
        var result = await fixture.Authorizer().AuthorizeAsync(fixture.Request("op"), CancellationToken.None);
        AssertSuperseded(result);
    }

    [Fact]
    public async Task ChildRevokedDuringRecording_IsSuperseded()
    {
        var fixture = new Fixture();
        fixture.Recorder = new RecordingRecorder(1, () => fixture.RevokeChild());
        var result = await fixture.Authorizer().AuthorizeAsync(fixture.Request("op"), CancellationToken.None);
        AssertSuperseded(result);
    }

    [Fact]
    public async Task ParentHeadMovesDuringRecording_IsSuperseded()
    {
        var fixture = new Fixture();
        fixture.Recorder = new RecordingRecorder(1, () => fixture.MoveParent());
        var result = await fixture.Authorizer().AuthorizeAsync(fixture.Request("op"), CancellationToken.None);
        AssertSuperseded(result);
    }

    [Fact]
    public async Task ParentDeniedDuringRecording_IsSuperseded()
    {
        var fixture = new Fixture();
        fixture.Recorder = new RecordingRecorder(1, () => fixture.DenyParent());
        var result = await fixture.Authorizer().AuthorizeAsync(fixture.Request("op"), CancellationToken.None);
        AssertSuperseded(result);
    }

    [Fact]
    public async Task LifecycleSequenceMovesDuringRecording_IsSuperseded()
    {
        var fixture = new Fixture();
        fixture.Recorder = new RecordingRecorder(2, () => fixture.MoveLifecycle(sequence: 4));
        var result = await fixture.Authorizer().AuthorizeAsync(fixture.Request("op"), CancellationToken.None);
        AssertSuperseded(result);
    }

    [Fact]
    public async Task LifecycleSuspendedDuringRecording_IsSuperseded()
    {
        var fixture = new Fixture();
        fixture.Recorder = new RecordingRecorder(2, () => fixture.MoveLifecycle(sequence: 4, ExecutionLifecycleState.Suspended));
        var result = await fixture.Authorizer().AuthorizeAsync(fixture.Request("op"), CancellationToken.None);
        AssertSuperseded(result);
    }

    [Fact]
    public async Task SupersededIsUnavailable_NotDeny()
    {
        var fixture = new Fixture();
        fixture.Recorder = new RecordingRecorder(1, () => fixture.MoveChild());
        var result = await fixture.Authorizer().AuthorizeAsync(fixture.Request("op"), CancellationToken.None);
        Assert.Equal(AuthorityStatus.Unavailable, result.Status);
        Assert.Equal("authority.read-set-superseded", result.Decision!.ReasonCode);
        Assert.False(result.IsAuthorized);
    }

    /// <summary>
    /// Proves the full sequence: evaluation produces a Permit, the mandatory
    /// Permit evidence is recorded, a contributing head moves, final
    /// revalidation fails, and the caller receives Unavailable. The recorded
    /// Permit must never be readable as a usable/delivered authorization.
    /// </summary>
    [Fact]
    public async Task RecordedPermitEvidence_CanBeSupersededBeforeReturn()
    {
        var fixture = new Fixture();
        var recorder = new RecordingRecorder(2, () => fixture.MoveLifecycle(sequence: 4));
        fixture.Recorder = recorder;

        var result = await fixture.Authorizer().AuthorizeAsync(fixture.Request("op"), CancellationToken.None);

        // The evaluator's Permit was captured as mandatory evidence...
        Assert.Contains(recorder.Decisions, decision => decision.Status == AuthorityStatus.Permit);
        // ...yet the delivered result is a superseded Unavailable with no usable Permit.
        Assert.False(result.IsAuthorized);
        Assert.Equal(AuthorityStatus.Unavailable, result.Status);
        Assert.Equal("authority.read-set-superseded", result.Decision!.ReasonCode);
        Assert.NotEqual(AuthorityStatus.Permit, result.Decision.Status);
    }

    private static void AssertSuperseded(AuthorityRequestAuthorization result)
    {
        Assert.False(result.IsAuthorized);
        Assert.Equal(AuthorityStatus.Unavailable, result.Status);
        Assert.Equal("authority.read-set-superseded", result.Decision!.ReasonCode);
    }

    private sealed class Fixture
    {
        private CurrentParentPolicyResult _parent = ParentResult("parent-v1", CurrentParentPolicyVerdict.Permit);
        private ExecutionLifecycleCheckResult _lifecycle = new(ExecutionLifecycleVerdict.Live, ExecutionLifecycleState.Active, 3);

        public MutableSource Source { get; } = new();
        public RecordingRecorder Recorder { get; set; } = new(1, () => { });

        public Fixture() => Source.Snapshot = ChildSnapshot("child-v1");

        public AuthorityRequest Request(string id) =>
            new(Child, AuthorityAction.ReadFile, "workspace", "src/service/a.cs", id);

        public void MoveChild() => Source.Snapshot = ChildSnapshot("child-v2");

        public void RevokeChild() => Source.Snapshot = null;

        public void MoveParent() => _parent = ParentResult("parent-v2", CurrentParentPolicyVerdict.Permit);

        public void DenyParent() => _parent = ParentResult("parent-v2", CurrentParentPolicyVerdict.ParentDenied);

        public void MoveLifecycle(long sequence, ExecutionLifecycleState state = ExecutionLifecycleState.Active) =>
            _lifecycle = new(ExecutionLifecycleVerdict.Live, state, sequence);

        public CurrentAuthorityRequestAuthorizer Authorizer() =>
            new(Source, new AllowEvaluator(), Recorder, new MutableTimeProvider(Now),
                ancestorLiveness: null,
                parentPolicy: new SwitchableParentPolicy(() => _parent),
                lifecycleGate: new SwitchableLifecycleGate(() => _lifecycle));

        private static AuthoritySnapshot ChildSnapshot(string version)
        {
            var grant = new AuthorityGrant("grant-child", [AuthorityAction.ReadFile],
                new AuthorityScope("workspace", "src/service", AuthorityScopeKind.Subtree), [],
                Now.AddMinutes(-1), Now.AddDays(1)) { ParentGrantId = "grant-parent" };
            return new AuthoritySnapshot(Child, version, [new AuthorityLayer("derived", [grant])], [], Now.AddDays(1));
        }

        private static CurrentParentPolicyResult ParentResult(string version, CurrentParentPolicyVerdict verdict)
        {
            var parentRequest = new AuthorityRequest(Supervisor, AuthorityAction.ReadFile, "workspace", "src/service/a.cs", "parent-op");
            var parentDecision = new AuthorityDecision(
                verdict == CurrentParentPolicyVerdict.Permit ? AuthorityStatus.Permit : AuthorityStatus.Deny,
                verdict == CurrentParentPolicyVerdict.Permit ? "authority.permitted" : "test.layer-denied",
                version, "test-parent-eval", new string('a', 64));
            return new(verdict, parentRequest, parentDecision, "grant-parent", version, new string('b', 64), "test-parent-eval", Now);
        }
    }

    private sealed class MutableSource : IAuthoritySnapshotSource
    {
        public AuthoritySnapshot? Snapshot { get; set; }
        public ValueTask<AuthoritySnapshot?> GetCurrentAsync(AuthenticatedAuthorityContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(Snapshot);
    }

    private sealed class SwitchableParentPolicy(Func<CurrentParentPolicyResult> resolve) : ICurrentParentPolicy
    {
        public ValueTask<CurrentParentPolicyResult> CheckAsync(AuthoritySnapshot snapshot, AuthorityRequest request,
            DateTimeOffset evaluatedAt, CancellationToken ct = default) => ValueTask.FromResult(resolve());
    }

    private sealed class SwitchableLifecycleGate(Func<ExecutionLifecycleCheckResult> resolve) : IExecutionLifecycleGate
    {
        public ValueTask<ExecutionLifecycleCheckResult> CheckAsync(ExecutionLifecycleBinding binding,
            DateTimeOffset evaluatedAt, CancellationToken ct = default) => ValueTask.FromResult(resolve());
    }

    private sealed class RecordingRecorder(int triggerCall, Action onRecord) : IAuthorityDecisionRecorder
    {
        private int _calls;
        public List<AuthorityDecision> Decisions { get; } = new();
        public ValueTask<bool> RecordAsync(AuthorityRequest request, AuthorityDecision decision, CancellationToken ct = default)
        {
            lock (Decisions) Decisions.Add(decision);
            if (Interlocked.Increment(ref _calls) == triggerCall) onRecord();
            return ValueTask.FromResult(true);
        }
    }

    private sealed class AllowEvaluator : IAuthorityEvaluator
    {
        public AuthorityDecision Evaluate(AuthoritySnapshot snapshot, AuthorityRequest request, DateTimeOffset now) =>
            new(AuthorityStatus.Permit, "authority.permitted", snapshot.Version, "test-evaluator-v1", snapshot.Identity);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}


