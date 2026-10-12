using Penghou.Hufu.Workflow;
using Xunit;

namespace Penghou.Hufu.Workflow.Tests;

/// <summary>
/// Authenticated workflow-event translation into neutral lifecycle transitions:
/// suspend/resume preserve the binding, cancel/complete/supersede close it,
/// duplicate events replay, forged labels and unavailable authentication fail
/// closed, and ordering is enforced.
/// </summary>
public sealed class WorkflowAuthorityLifecycleTranslatorTests
{
    private const string Source = "zhinu";
    private const string Tenant = "tenant";
    private const string Subject = "delegation";
    private const string Run = "run-child";

    [Fact]
    public async Task SuspendThenResume_PreservesBinding()
    {
        var store = new InMemoryStore();
        var translator = new WorkflowAuthorityLifecycleTranslator(new Trust(principal => principal), store);

        Assert.Equal(ExecutionLifecycleApplyStatus.Applied, (await translator.ApplyAsync(Event(WorkflowAuthorityEventKind.Suspend, "e1", 0))).Status);
        Assert.Equal(ExecutionLifecycleState.Suspended, await store.GetStateAsync(Binding()));

        Assert.Equal(ExecutionLifecycleApplyStatus.Applied, (await translator.ApplyAsync(Event(WorkflowAuthorityEventKind.Resume, "e2", 1))).Status);
        Assert.Equal(ExecutionLifecycleState.Active, await store.GetStateAsync(Binding()));
    }

    [Theory]
    [InlineData(WorkflowAuthorityEventKind.Cancel, ExecutionLifecycleTerminalReason.Cancelled)]
    [InlineData(WorkflowAuthorityEventKind.Complete, ExecutionLifecycleTerminalReason.Completed)]
    [InlineData(WorkflowAuthorityEventKind.Supersede, ExecutionLifecycleTerminalReason.Superseded)]
    public async Task TerminalEvents_CloseBinding(WorkflowAuthorityEventKind kind, ExecutionLifecycleTerminalReason reason)
    {
        var store = new InMemoryStore();
        var translator = new WorkflowAuthorityLifecycleTranslator(new Trust(principal => principal), store);
        var result = await translator.ApplyAsync(Event(kind, "e1", 0));
        Assert.Equal(ExecutionLifecycleApplyStatus.Applied, result.Status);
        Assert.Equal(reason, result.Receipt!.Reason);
        Assert.Equal(ExecutionLifecycleState.Terminal, await store.GetStateAsync(Binding()));
    }

    [Fact]
    public async Task DuplicateEvent_ReplaysOriginalReceipt()
    {
        var store = new InMemoryStore();
        var translator = new WorkflowAuthorityLifecycleTranslator(new Trust(principal => principal), store);
        var first = await translator.ApplyAsync(Event(WorkflowAuthorityEventKind.Suspend, "e1", 0));
        var replay = await translator.ApplyAsync(Event(WorkflowAuthorityEventKind.Suspend, "e1", 0));
        Assert.Equal(ExecutionLifecycleApplyStatus.Replayed, replay.Status);
        Assert.Equal(first.Receipt, replay.Receipt);
    }

    [Fact]
    public async Task ForgedLabel_IsConflict()
    {
        var store = new InMemoryStore();
        // Authenticated run differs from the claimed label.
        var translator = new WorkflowAuthorityLifecycleTranslator(
            new Trust(principal => principal with { RunId = "other-run" }), store);
        var result = await translator.ApplyAsync(Event(WorkflowAuthorityEventKind.Suspend, "e1", 0));
        Assert.Equal(ExecutionLifecycleApplyStatus.Conflict, result.Status);
        Assert.Null(await store.GetStateAsync(Binding()));
    }

    [Fact]
    public async Task UnauthenticatedEvent_IsUnavailable()
    {
        var store = new InMemoryStore();
        var translator = new WorkflowAuthorityLifecycleTranslator(new Trust(_ => null), store);
        var result = await translator.ApplyAsync(Event(WorkflowAuthorityEventKind.Suspend, "e1", 0));
        Assert.Equal(ExecutionLifecycleApplyStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task StaleEvent_IsRejected()
    {
        var store = new InMemoryStore();
        var translator = new WorkflowAuthorityLifecycleTranslator(new Trust(principal => principal), store);
        await translator.ApplyAsync(Event(WorkflowAuthorityEventKind.Suspend, "e1", 0));
        var stale = await translator.ApplyAsync(Event(WorkflowAuthorityEventKind.Resume, "e2", 0));
        Assert.Equal(ExecutionLifecycleApplyStatus.Stale, stale.Status);
    }

    [Fact]
    public async Task NullEvent_IsInvalidRequest()
    {
        var translator = new WorkflowAuthorityLifecycleTranslator(new Trust(principal => principal), new InMemoryStore());
        Assert.Equal(ExecutionLifecycleApplyStatus.InvalidRequest, (await translator.ApplyAsync(null!)).Status);
    }

    [Fact]
    public async Task SupersedeRetiresOldRun_NewRunUnaffected()
    {
        var store = new InMemoryStore();
        var translator = new WorkflowAuthorityLifecycleTranslator(new Trust(principal => principal), store);
        await translator.ApplyAsync(Event(WorkflowAuthorityEventKind.Supersede, "e1", 0));
        Assert.Equal(ExecutionLifecycleState.Terminal, await store.GetStateAsync(Binding()));

        // A new generation is a new run; the old binding's terminal state does not affect it.
        var newRun = new ExecutionLifecycleBinding(Tenant, Subject, "run-child-2");
        Assert.Null(await store.GetStateAsync(newRun));
    }

    private static ExecutionLifecycleBinding Binding() => new(Tenant, Subject, Run);

    private static WorkflowAuthorityEvent Event(WorkflowAuthorityEventKind kind, string eventIdentity, long expected) =>
        new(Source, Tenant, Subject, Run, "activity-1", "delegation-1", "gen-1", "rev-1", "fence-1",
            eventIdentity, eventOrder: expected, expectedAuthoritySequence: expected, kind, reason: kind.ToString());

    private sealed class Trust(Func<WorkflowAuthorityEventPrincipal, WorkflowAuthorityEventPrincipal?> mutate) : IWorkflowAuthorityEventTrustSource
    {
        public ValueTask<WorkflowAuthorityEventPrincipal?> AuthenticateAsync(WorkflowAuthorityEvent @event, CancellationToken ct = default)
        {
            var principal = new WorkflowAuthorityEventPrincipal(@event.Source, @event.TenantId, @event.SubjectId, @event.RunId,
                @event.ActivityId, @event.DelegationId, @event.Generation, @event.RevisionId, @event.FenceId, @event.EventIdentity);
            return new(mutate(principal));
        }
    }

    private sealed class InMemoryStore : IExecutionLifecycleStore
    {
        private readonly Dictionary<string, Row> _byBinding = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Row> _byTransition = new(StringComparer.Ordinal);

        public ValueTask<ExecutionLifecycleApplyResult> ApplyAsync(ExecutionLifecycleTransition transition, CancellationToken ct = default)
        {
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

        public ValueTask<ExecutionLifecycleState?> GetStateAsync(ExecutionLifecycleBinding binding, CancellationToken ct = default) =>
            ValueTask.FromResult(_byBinding.TryGetValue(Key(binding), out var row) ? row.State : (ExecutionLifecycleState?)null);

        private static string Key(ExecutionLifecycleBinding binding) =>
            binding.TenantId + "\0" + binding.SubjectId + "\0" + binding.RunId;

        private sealed record Row(ExecutionLifecycleBinding Binding, long Sequence, ExecutionLifecycleState State,
            ExecutionLifecycleTransitionKind Kind, ExecutionLifecycleTerminalReason Reason,
            string TransitionId, string Generation, string Fence)
        {
            internal ExecutionLifecycleReceipt Receipt() =>
                new(Binding, Sequence, State, Kind, Reason, TransitionId, Generation, Fence, DateTimeOffset.UnixEpoch);
        }
    }
}
