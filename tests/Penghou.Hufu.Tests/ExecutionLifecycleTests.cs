using Xunit;

namespace Penghou.Hufu.Tests;

/// <summary>
/// AE-1.2 lifecycle authority state: suspend/resume preserve the same binding,
/// cancel/complete/supersede close it, duplicate transitions replay, ordering
/// is enforced, and the gate fails closed on store failure. The consumption
/// wiring applies the gate only to derived grants.
/// </summary>
public sealed class ExecutionLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly AuthenticatedAuthorityContext Supervisor =
        new("tenant", "supervisor", "run-sup", "rev", "fence");
    private static readonly AuthenticatedAuthorityContext Child =
        new("tenant", "delegation", "run-child", "rev", "fence");
    private static readonly ExecutionLifecycleBinding Binding = ExecutionLifecycleBinding.FromContext(Child);

    [Fact]
    public async Task AbsenceOfLifecycleFact_IsLive()
    {
        var gate = new StoreExecutionLifecycleGate(new InMemoryLifecycleStore());
        Assert.Equal(ExecutionLifecycleVerdict.Live, await gate.CheckAsync(Binding, Now));
    }

    [Fact]
    public async Task SuspendThenResume_PreservesSameBinding()
    {
        var store = new InMemoryLifecycleStore();
        var gate = new StoreExecutionLifecycleGate(store);

        var suspended = await store.ApplyAsync(Transition("t1", 0, ExecutionLifecycleTransitionKind.Suspend));
        Assert.Equal(ExecutionLifecycleApplyStatus.Applied, suspended.Status);
        Assert.Equal(1, suspended.Receipt!.Sequence);
        Assert.Equal(ExecutionLifecycleState.Suspended, suspended.Receipt.State);
        Assert.Equal(ExecutionLifecycleVerdict.Suspended, await gate.CheckAsync(Binding, Now));

        var resumed = await store.ApplyAsync(Transition("t2", 1, ExecutionLifecycleTransitionKind.Resume));
        Assert.Equal(ExecutionLifecycleApplyStatus.Applied, resumed.Status);
        Assert.Equal(2, resumed.Receipt!.Sequence);
        Assert.Equal(ExecutionLifecycleState.Active, resumed.Receipt.State);
        Assert.Equal(ExecutionLifecycleVerdict.Live, await gate.CheckAsync(Binding, Now));
    }

    [Theory]
    [InlineData(ExecutionLifecycleTerminalReason.Cancelled)]
    [InlineData(ExecutionLifecycleTerminalReason.Completed)]
    [InlineData(ExecutionLifecycleTerminalReason.Superseded)]
    public async Task TerminalReasons_CloseTheBinding(ExecutionLifecycleTerminalReason reason)
    {
        var store = new InMemoryLifecycleStore();
        var gate = new StoreExecutionLifecycleGate(store);
        var result = await store.ApplyAsync(Transition("t1", 0, ExecutionLifecycleTransitionKind.Terminate, reason));
        Assert.Equal(ExecutionLifecycleApplyStatus.Applied, result.Status);
        Assert.Equal(reason, result.Receipt!.Reason);
        Assert.Equal(ExecutionLifecycleVerdict.Terminal, await gate.CheckAsync(Binding, Now));

        // A later suspend/resume on a terminal binding cannot reopen it.
        var suspend = await store.ApplyAsync(Transition("t2", 1, ExecutionLifecycleTransitionKind.Suspend));
        Assert.Equal(ExecutionLifecycleApplyStatus.Terminated, suspend.Status);
        Assert.Equal(ExecutionLifecycleVerdict.Terminal, await gate.CheckAsync(Binding, Now));
    }

    [Fact]
    public async Task DuplicateTransition_ReplaysOriginalReceipt()
    {
        var store = new InMemoryLifecycleStore();
        var first = await store.ApplyAsync(Transition("t1", 0, ExecutionLifecycleTransitionKind.Suspend));
        var replay = await store.ApplyAsync(Transition("t1", 0, ExecutionLifecycleTransitionKind.Suspend));
        Assert.Equal(ExecutionLifecycleApplyStatus.Replayed, replay.Status);
        Assert.Equal(first.Receipt, replay.Receipt);
        Assert.Equal(1, replay.Receipt!.Sequence);
    }

    [Fact]
    public async Task SameTransitionIdDifferentIntent_Conflicts()
    {
        var store = new InMemoryLifecycleStore();
        await store.ApplyAsync(Transition("t1", 0, ExecutionLifecycleTransitionKind.Suspend));
        var conflict = await store.ApplyAsync(Transition("t1", 0, ExecutionLifecycleTransitionKind.Terminate,
            ExecutionLifecycleTerminalReason.Cancelled));
        Assert.Equal(ExecutionLifecycleApplyStatus.Conflict, conflict.Status);
    }

    [Fact]
    public async Task StaleExpectedSequence_IsRejected()
    {
        var store = new InMemoryLifecycleStore();
        await store.ApplyAsync(Transition("t1", 0, ExecutionLifecycleTransitionKind.Suspend));
        var stale = await store.ApplyAsync(Transition("t2", 0, ExecutionLifecycleTransitionKind.Resume));
        Assert.Equal(ExecutionLifecycleApplyStatus.Stale, stale.Status);
    }

    [Fact]
    public async Task InvalidTransitions_Conflict()
    {
        var store = new InMemoryLifecycleStore();
        // Resume with no prior suspend.
        var resume = await store.ApplyAsync(Transition("t1", 0, ExecutionLifecycleTransitionKind.Resume));
        Assert.Equal(ExecutionLifecycleApplyStatus.Conflict, resume.Status);
        // Suspend when already suspended.
        await store.ApplyAsync(Transition("t2", 0, ExecutionLifecycleTransitionKind.Suspend));
        var suspend = await store.ApplyAsync(Transition("t3", 1, ExecutionLifecycleTransitionKind.Suspend));
        Assert.Equal(ExecutionLifecycleApplyStatus.Conflict, suspend.Status);
    }

    [Fact]
    public async Task StoreFailure_GateIsUnavailable()
    {
        var store = new InMemoryLifecycleStore { Throw = true };
        var gate = new StoreExecutionLifecycleGate(store);
        Assert.Equal(ExecutionLifecycleVerdict.Unavailable, await gate.CheckAsync(Binding, Now));
    }

    [Fact]
    public async Task SuspendDeniesDerivedUse_ResumeRestores_TerminateCloses()
    {
        var fixture = new DerivedFixture();
        var store = new InMemoryLifecycleStore();
        var gate = new StoreExecutionLifecycleGate(store);
        var authorizer = fixture.Create(gate);
        var request = fixture.ChildRequest("op");

        Assert.True((await authorizer.AuthorizeAsync(request)).IsAuthorized);

        await store.ApplyAsync(Transition("s1", 0, ExecutionLifecycleTransitionKind.Suspend));
        var suspended = await authorizer.AuthorizeAsync(request with { RequestIdentity = "op-s" });
        Assert.False(suspended.IsAuthorized);
        Assert.Equal("authority.lifecycle-suspended", suspended.Decision!.ReasonCode);

        await store.ApplyAsync(Transition("s2", 1, ExecutionLifecycleTransitionKind.Resume));
        Assert.True((await authorizer.AuthorizeAsync(request with { RequestIdentity = "op-r" })).IsAuthorized);

        await store.ApplyAsync(Transition("s3", 2, ExecutionLifecycleTransitionKind.Terminate,
            ExecutionLifecycleTerminalReason.Cancelled));
        var terminal = await authorizer.AuthorizeAsync(request with { RequestIdentity = "op-t" });
        Assert.False(terminal.IsAuthorized);
        Assert.Equal("authority.lifecycle-terminal", terminal.Decision!.ReasonCode);
    }

    [Fact]
    public async Task LifecycleUnavailable_YieldsNoPermit()
    {
        var fixture = new DerivedFixture();
        var store = new InMemoryLifecycleStore { Throw = true };
        var authorizer = fixture.Create(new StoreExecutionLifecycleGate(store));
        var result = await authorizer.AuthorizeAsync(fixture.ChildRequest("op"));
        Assert.False(result.IsAuthorized);
        Assert.Equal(AuthorityStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task RootUseIgnoresLifecycleGate()
    {
        var fixture = new DerivedFixture();
        var store = new InMemoryLifecycleStore();
        await store.ApplyAsync(new ExecutionLifecycleTransition(
            ExecutionLifecycleBinding.FromContext(Supervisor), 0, "r1",
            ExecutionLifecycleTransitionKind.Terminate, ExecutionLifecycleTerminalReason.Cancelled, "gen-1", "fence"));
        var authorizer = fixture.Create(new StoreExecutionLifecycleGate(store));
        var root = await authorizer.AuthorizeAsync(new AuthorityRequest(Supervisor, AuthorityAction.ReadFile,
            "workspace", "src/a.cs", "op-root"));
        Assert.True(root.IsAuthorized);
    }

    private static ExecutionLifecycleTransition Transition(string id, long expected,
        ExecutionLifecycleTransitionKind kind, ExecutionLifecycleTerminalReason reason = ExecutionLifecycleTerminalReason.None) =>
        new(Binding, expected, id, kind, reason, "gen-1", "fence");

    private sealed class DerivedFixture
    {
        private readonly AuthoritySnapshot _child;
        private readonly AuthoritySnapshot _parent;
        private readonly AuthorityGrantLineage _lineage;

        public DerivedFixture()
        {
            var childGrant = new AuthorityGrant("grant-child", [AuthorityAction.ReadFile],
                new AuthorityScope("workspace", "src/service", AuthorityScopeKind.Subtree), [],
                Now.AddMinutes(-1), Now.AddDays(1)) { ParentGrantId = "grant-parent" };
            _child = new AuthoritySnapshot(Child, "child-v1", [new AuthorityLayer("derived", [childGrant])], [], Now.AddDays(1));
            var parentGrant = new AuthorityGrant("grant-parent", [AuthorityAction.ReadFile],
                new AuthorityScope("workspace", "src", AuthorityScopeKind.Subtree), [],
                Now.AddMinutes(-1), Now.AddDays(1));
            _parent = new AuthoritySnapshot(Supervisor, "parent-v1", [new AuthorityLayer("run", [parentGrant])], [], Now.AddDays(1));
            _lineage = new AuthorityGrantLineage("d1", "grant-child", Child, "grant-parent", Supervisor,
                "delegation-1", "gen-1", new RequestedAuthority([AuthorityAction.ReadFile],
                    new AuthorityScope("workspace", "src/service", AuthorityScopeKind.Subtree), [], Now.AddMinutes(-1), Now.AddDays(1)),
                new AuthorityStoreActor("tenant", "host", "session-1"), Now);
        }

        public AuthorityRequest ChildRequest(string id) =>
            new(Child, AuthorityAction.ReadFile, "workspace", "src/service/a.cs", id);

        public CurrentAuthorityRequestAuthorizer Create(IExecutionLifecycleGate? gate)
        {
            var source = new MapSource(context => context == Supervisor ? _parent : _child);
            var derivations = new FixedDerivations(_lineage);
            return DerivedAuthorityRequestAuthorizer.Create(source, new CoverEvaluator(), new AllowRecorder(), derivations,
                new MutableTimeProvider(Now), gate);
        }
    }

    private sealed class InMemoryLifecycleStore : IExecutionLifecycleStore
    {
        private readonly Dictionary<string, Row> _byBinding = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Row> _byTransition = new(StringComparer.Ordinal);
        public bool Throw;

        public ValueTask<ExecutionLifecycleApplyResult> ApplyAsync(ExecutionLifecycleTransition transition, CancellationToken ct = default)
        {
            if (Throw) throw new InvalidOperationException("lifecycle store unavailable");
            if (_byTransition.TryGetValue(transition.TransitionId, out var existing))
            {
                var sameIntent = existing.Binding == transition.Binding && existing.Kind == transition.Kind &&
                    existing.Reason == transition.Reason && existing.Generation == transition.Generation &&
                    existing.Fence == transition.Fence;
                return ValueTask.FromResult(new ExecutionLifecycleApplyResult(
                    sameIntent ? ExecutionLifecycleApplyStatus.Replayed : ExecutionLifecycleApplyStatus.Conflict, existing.Receipt()));
            }
            var key = Key(transition.Binding);
            _byBinding.TryGetValue(key, out var current);
            var sequence = current?.Sequence ?? 0;
            if (transition.ExpectedSequence != sequence)
                return ValueTask.FromResult(new ExecutionLifecycleApplyResult(ExecutionLifecycleApplyStatus.Stale, current?.Receipt()));
            if (current?.State == ExecutionLifecycleState.Terminal)
                return ValueTask.FromResult(new ExecutionLifecycleApplyResult(ExecutionLifecycleApplyStatus.Terminated, current.Receipt()));
            ExecutionLifecycleState next;
            switch (transition.Kind)
            {
                case ExecutionLifecycleTransitionKind.Suspend:
                    if (current?.State == ExecutionLifecycleState.Suspended)
                        return ValueTask.FromResult(new ExecutionLifecycleApplyResult(ExecutionLifecycleApplyStatus.Conflict, current.Receipt()));
                    next = ExecutionLifecycleState.Suspended;
                    break;
                case ExecutionLifecycleTransitionKind.Resume:
                    if (current?.State != ExecutionLifecycleState.Suspended)
                        return ValueTask.FromResult(new ExecutionLifecycleApplyResult(ExecutionLifecycleApplyStatus.Conflict, current?.Receipt()));
                    next = ExecutionLifecycleState.Active;
                    break;
                case ExecutionLifecycleTransitionKind.Terminate:
                    next = ExecutionLifecycleState.Terminal;
                    break;
                default:
                    return ValueTask.FromResult(new ExecutionLifecycleApplyResult(ExecutionLifecycleApplyStatus.InvalidRequest));
            }
            var row = new Row(transition.Binding, sequence + 1, next, transition.Kind, transition.Reason,
                transition.TransitionId, transition.Generation, transition.Fence);
            _byBinding[key] = row;
            _byTransition[transition.TransitionId] = row;
            return ValueTask.FromResult(new ExecutionLifecycleApplyResult(ExecutionLifecycleApplyStatus.Applied, row.Receipt()));
        }

        public ValueTask<ExecutionLifecycleState?> GetStateAsync(ExecutionLifecycleBinding binding, CancellationToken ct = default)
        {
            if (Throw) throw new InvalidOperationException("lifecycle store unavailable");
            return ValueTask.FromResult(_byBinding.TryGetValue(Key(binding), out var row) ? row.State : (ExecutionLifecycleState?)null);
        }

        private static string Key(ExecutionLifecycleBinding binding) =>
            binding.TenantId + "\0" + binding.SubjectId + "\0" + binding.RunId;

        private sealed record Row(ExecutionLifecycleBinding Binding, long Sequence, ExecutionLifecycleState State,
            ExecutionLifecycleTransitionKind Kind, ExecutionLifecycleTerminalReason Reason,
            string TransitionId, string Generation, string Fence)
        {
            internal ExecutionLifecycleReceipt Receipt() =>
                new(Binding, Sequence, State, Kind, Reason, TransitionId, Generation, Fence, Now);
        }
    }

    private sealed class MapSource(Func<AuthenticatedAuthorityContext, AuthoritySnapshot?> resolve) : IAuthoritySnapshotSource
    {
        public ValueTask<AuthoritySnapshot?> GetCurrentAsync(AuthenticatedAuthorityContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(resolve(context));
    }

    private sealed class FixedDerivations(AuthorityGrantLineage lineage) : IAuthorityDerivationStore
    {
        public ValueTask<AuthorityGrantDerivationResult> DeriveAsync(AuthorityDerivationCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public ValueTask<AuthorityGrantLineage?> GetLineageAsync(string childGrantId, CancellationToken ct = default) =>
            ValueTask.FromResult<AuthorityGrantLineage?>(childGrantId == lineage.ChildGrantId ? lineage : null);
    }

    private sealed class CoverEvaluator : IAuthorityEvaluator
    {
        public AuthorityDecision Evaluate(AuthoritySnapshot snapshot, AuthorityRequest request, DateTimeOffset now)
        {
            var covered = snapshot.Layers.SelectMany(layer => layer.Grants).Any(grant =>
                grant.Actions.Contains(request.Action) && grant.NotBefore <= now && now < grant.ExpiresAt &&
                AuthorityValidation.Contains(grant.Scope, new AuthorityScope(request.WorkspaceId,
                    AuthorityValidation.NormalizePath(request.RelativePath), AuthorityScopeKind.Exact)));
            return new(covered ? AuthorityStatus.Permit : AuthorityStatus.Deny,
                covered ? "test.permitted" : "test.layer-denied", snapshot.Version, "test-evaluator-v1", snapshot.Identity);
        }
    }

    private sealed class AllowRecorder : IAuthorityDecisionRecorder
    {
        public ValueTask<bool> RecordAsync(AuthorityRequest request, AuthorityDecision decision, CancellationToken ct = default) =>
            new(true);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
