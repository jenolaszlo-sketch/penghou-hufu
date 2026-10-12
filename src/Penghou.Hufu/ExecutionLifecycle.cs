namespace Penghou.Hufu;

/// <summary>
/// Generic execution-lifecycle authority state. Hufu owns whether execution
/// authority for one binding is live; a workflow runtime owns why. This is
/// distinct from terminal store revocation of grant identity and from
/// visibility (Observe/Cancel/Intervene) authority.
/// </summary>
public enum ExecutionLifecycleState
{
    /// <summary>Execution use is open subject to ordinary current authority.</summary>
    Active = 0,
    /// <summary>New protected uses are quiesced; the same generation/binding is preserved.</summary>
    Suspended = 1,
    /// <summary>Execution use is permanently closed for this binding.</summary>
    Terminal = 2
}

/// <summary>Neutral lifecycle transition kinds. Workflow vocabulary stays in the adapter.</summary>
public enum ExecutionLifecycleTransitionKind { Suspend = 0, Resume = 1, Terminate = 2 }

/// <summary>Why an execution binding became terminal. The runtime may retain richer detail separately.</summary>
public enum ExecutionLifecycleTerminalReason { None = 0, Cancelled = 1, Completed = 2, Superseded = 3, Expired = 4 }

/// <summary>Outcome of applying one neutral lifecycle transition.</summary>
public enum ExecutionLifecycleApplyStatus
{
    InvalidRequest = 0,
    Unavailable = 1,
    Applied = 2,
    Replayed = 3,
    Conflict = 4,
    Stale = 5,
    Terminated = 6
}

/// <summary>The lifecycle-gate verdict for one binding at one evaluated instant.</summary>
public enum ExecutionLifecycleVerdict { Live = 0, Suspended = 1, Terminal = 2, Unavailable = 3 }

/// <summary>
/// The stable execution identity whose lifecycle authority is tracked. It binds
/// the authenticated execution subject and run; a new generation or superseded
/// fence is a new binding, not a mutation of this one.
/// </summary>
public sealed record ExecutionLifecycleBinding
{
    public ExecutionLifecycleBinding(string tenantId, string subjectId, string runId)
    {
        if (!AuthorityValidation.ValidToken(tenantId))
            throw new ArgumentException("A bounded tenant identity is required.", nameof(tenantId));
        if (!AuthorityValidation.ValidToken(subjectId))
            throw new ArgumentException("A bounded subject identity is required.", nameof(subjectId));
        if (!AuthorityValidation.ValidToken(runId))
            throw new ArgumentException("A bounded run identity is required.", nameof(runId));
        TenantId = tenantId;
        SubjectId = subjectId;
        RunId = runId;
    }

    public string TenantId { get; }
    public string SubjectId { get; }
    public string RunId { get; }

    public static ExecutionLifecycleBinding FromContext(AuthenticatedAuthorityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new(context.TenantId, context.SubjectId, context.RunId);
    }
}

/// <summary>
/// One neutral, host-authenticated lifecycle transition to apply durably.
/// Ordering is expressed by <see cref="ExpectedSequence"/> (compare-and-set);
/// replay is expressed by <see cref="TransitionId"/>. Source evidence is
/// opaque provenance and never carries workflow semantics into core.
/// </summary>
public sealed record ExecutionLifecycleTransition
{
    public ExecutionLifecycleTransition(
        ExecutionLifecycleBinding binding,
        long expectedSequence,
        string transitionId,
        ExecutionLifecycleTransitionKind kind,
        ExecutionLifecycleTerminalReason reason,
        string generation,
        string fence,
        string? sourceEvidence = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (expectedSequence < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedSequence));
        if (!AuthorityValidation.ValidToken(transitionId))
            throw new ArgumentException("A bounded transition identity is required.", nameof(transitionId));
        if (!Enum.IsDefined(kind))
            throw new ArgumentException("A known transition kind is required.", nameof(kind));
        if (!Enum.IsDefined(reason))
            throw new ArgumentException("A known terminal reason is required.", nameof(reason));
        if (kind == ExecutionLifecycleTransitionKind.Terminate)
        {
            if (reason == ExecutionLifecycleTerminalReason.None)
                throw new ArgumentException("A terminal transition requires a reason.", nameof(reason));
        }
        else if (reason != ExecutionLifecycleTerminalReason.None)
        {
            throw new ArgumentException("Only a terminal transition carries a reason.", nameof(reason));
        }
        if (!AuthorityValidation.ValidToken(generation))
            throw new ArgumentException("A bounded generation identity is required.", nameof(generation));
        if (!AuthorityValidation.ValidToken(fence))
            throw new ArgumentException("A bounded fence identity is required.", nameof(fence));
        if (sourceEvidence is not null && !AuthorityValidation.ValidToken(sourceEvidence))
            throw new ArgumentException("Bounded source evidence is required.", nameof(sourceEvidence));
        Binding = binding;
        ExpectedSequence = expectedSequence;
        TransitionId = transitionId;
        Kind = kind;
        Reason = reason;
        Generation = generation;
        Fence = fence;
        SourceEvidence = sourceEvidence;
    }

    public ExecutionLifecycleBinding Binding { get; }
    public long ExpectedSequence { get; }
    public string TransitionId { get; }
    public ExecutionLifecycleTransitionKind Kind { get; }
    public ExecutionLifecycleTerminalReason Reason { get; }
    public string Generation { get; }
    public string Fence { get; }
    public string? SourceEvidence { get; }
}

/// <summary>Durable, idempotent receipt for one applied neutral lifecycle transition.</summary>
public sealed record ExecutionLifecycleReceipt(
    ExecutionLifecycleBinding Binding,
    long Sequence,
    ExecutionLifecycleState State,
    ExecutionLifecycleTransitionKind Kind,
    ExecutionLifecycleTerminalReason Reason,
    string TransitionId,
    string Generation,
    string Fence,
    DateTimeOffset RecordedAt);

/// <summary>The typed result of one lifecycle transition application.</summary>
public sealed record ExecutionLifecycleApplyResult(
    ExecutionLifecycleApplyStatus Status,
    ExecutionLifecycleReceipt? Receipt = null);

/// <summary>
/// Durable, ordered, idempotent persistence of neutral execution-lifecycle
/// authority state. It stores neutral transitions, never workflow events; the
/// trusted translator authenticates the source event before applying.
/// </summary>
public interface IExecutionLifecycleStore
{
    ValueTask<ExecutionLifecycleApplyResult> ApplyAsync(
        ExecutionLifecycleTransition transition,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the current state, or null when no lifecycle transition exists for the binding.</summary>
    ValueTask<ExecutionLifecycleState?> GetStateAsync(
        ExecutionLifecycleBinding binding,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Required fail-closed gate consulted by qualified derived-authority
/// consumption. Absence of any lifecycle fact is live; a suspended or terminal
/// binding cannot yield a usable Permit. A store failure is unavailable.
/// </summary>
public interface IExecutionLifecycleGate
{
    ValueTask<ExecutionLifecycleVerdict> CheckAsync(
        ExecutionLifecycleBinding binding,
        DateTimeOffset evaluatedAt,
        CancellationToken cancellationToken = default);
}

/// <summary>Lifecycle gate over a durable lifecycle store.</summary>
public sealed class StoreExecutionLifecycleGate : IExecutionLifecycleGate
{
    private readonly IExecutionLifecycleStore _store;

    public StoreExecutionLifecycleGate(IExecutionLifecycleStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    public async ValueTask<ExecutionLifecycleVerdict> CheckAsync(
        ExecutionLifecycleBinding binding,
        DateTimeOffset evaluatedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        try
        {
            var state = await _store.GetStateAsync(binding, cancellationToken).ConfigureAwait(false);
            return state switch
            {
                null or ExecutionLifecycleState.Active => ExecutionLifecycleVerdict.Live,
                ExecutionLifecycleState.Suspended => ExecutionLifecycleVerdict.Suspended,
                ExecutionLifecycleState.Terminal => ExecutionLifecycleVerdict.Terminal,
                _ => ExecutionLifecycleVerdict.Unavailable
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return ExecutionLifecycleVerdict.Unavailable; }
    }
}
