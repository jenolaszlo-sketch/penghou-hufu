namespace Penghou.Hufu;

/// <summary>
/// The narrow delegated-work acceptance operation. Distinct from
/// <see cref="AuthorityStoreOperation.Derive"/> (may create/attenuate authority)
/// and from resource actions (may perform an operation). Accepting work never
/// grants resource rights by implication.
/// </summary>
public enum DelegationAcceptanceOperation { Accept = 0 }

/// <summary>
/// One bounded delegated-work acceptance request. The workspace is a constraint
/// on what is being accepted, not the authority itself. The caller selects none
/// of these facts; the host must bind them from authenticated state.
/// </summary>
public sealed record DelegationAcceptanceRequest
{
    public DelegationAcceptanceRequest(AuthenticatedAuthorityContext context, string acceptanceIdentity,
        string workspaceId, string provider, string generation, string boundedRequestDigest)
    {
        if (!AuthorityValidation.ValidContext(context))
            throw new ArgumentException("A bounded authenticated context is required.", nameof(context));
        if (!AuthorityValidation.ValidToken(acceptanceIdentity))
            throw new ArgumentException("A bounded acceptance identity is required.", nameof(acceptanceIdentity));
        if (!AuthorityValidation.ValidToken(workspaceId))
            throw new ArgumentException("A bounded workspace ceiling is required.", nameof(workspaceId));
        if (!AuthorityValidation.ValidToken(provider))
            throw new ArgumentException("A bounded provider identity is required.", nameof(provider));
        if (!AuthorityValidation.ValidToken(generation))
            throw new ArgumentException("A bounded generation identity is required.", nameof(generation));
        if (boundedRequestDigest is not { Length: 64 } || !boundedRequestDigest.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new ArgumentException("A lowercase SHA-256 request digest is required.", nameof(boundedRequestDigest));
        Context = context;
        AcceptanceIdentity = acceptanceIdentity;
        WorkspaceId = workspaceId;
        Provider = provider;
        Generation = generation;
        BoundedRequestDigest = boundedRequestDigest;
    }

    public AuthenticatedAuthorityContext Context { get; }
    public string AcceptanceIdentity { get; }
    public string WorkspaceId { get; }
    public string Provider { get; }
    public string Generation { get; }
    public string BoundedRequestDigest { get; }
}

/// <summary>Host policy for one acceptance request. Never authenticates the caller or binds resources.</summary>
public interface IDelegationAcceptanceEvaluator
{
    AuthorityDecision Evaluate(AuthoritySnapshot snapshot, DelegationAcceptanceRequest request, DateTimeOffset now);
}

/// <summary>Required attributable acceptance evidence; failure blocks acceptance.</summary>
public interface IDelegationAcceptanceRecorder
{
    ValueTask<bool> RecordAsync(DelegationAcceptanceRequest request, AuthorityDecision decision,
        CancellationToken cancellationToken = default);
}

/// <summary>Current-authority acceptance decision. Never authorizes protected dispatch by itself.</summary>
public sealed record DelegationAcceptanceAuthorization(DelegationAcceptanceRequest Request, AuthorityStatus Status,
    AuthorityDecision? Decision = null, bool EvidenceRecorded = false)
{
    public bool IsAuthorized => Status == AuthorityStatus.Permit && Decision?.Status == AuthorityStatus.Permit &&
        EvidenceRecorded && AuthorityValidation.ValidToken(Decision.ReasonCode) &&
        AuthorityValidation.ValidToken(Decision.SnapshotVersion) && AuthorityValidation.ValidToken(Decision.EvaluatorIdentity) &&
        Decision.SnapshotIdentity is { Length: 64 } hash && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>The accepted operation for delegated-work admission.</summary>
public interface IDelegationAcceptanceAuthorizer
{
    ValueTask<DelegationAcceptanceAuthorization> AuthorizeAsync(DelegationAcceptanceRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Current-authority acceptance gate. It mirrors the qualified request
/// composition — current snapshot, evaluator, optional lifecycle gate, mandatory
/// evidence and freshness — but over the acceptance operation, so acceptance can
/// never be inferred from resource or Derive authority.
/// </summary>
public sealed class CurrentDelegationAcceptanceAuthorizer : IDelegationAcceptanceAuthorizer
{
    private readonly IAuthoritySnapshotSource _source;
    private readonly IDelegationAcceptanceEvaluator _evaluator;
    private readonly IDelegationAcceptanceRecorder _recorder;
    private readonly TimeProvider _clock;
    private readonly IExecutionLifecycleGate? _lifecycleGate;

    public CurrentDelegationAcceptanceAuthorizer(IAuthoritySnapshotSource source,
        IDelegationAcceptanceEvaluator evaluator, IDelegationAcceptanceRecorder recorder,
        TimeProvider? clock = null, IExecutionLifecycleGate? lifecycleGate = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _clock = clock ?? TimeProvider.System;
        _lifecycleGate = lifecycleGate;
    }

    public async ValueTask<DelegationAcceptanceAuthorization> AuthorizeAsync(DelegationAcceptanceRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null) return new(request!, AuthorityStatus.Deny);
        try
        {
            var snapshot = await _source.GetCurrentAsync(request.Context, cancellationToken).AsTask()
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot is null || snapshot.Context != request.Context) return new(request, AuthorityStatus.Unavailable);
            var now = _clock.GetUtcNow();
            var decision = snapshot.ValidUntil <= now
                ? new AuthorityDecision(AuthorityStatus.Deny, "authority.snapshot-expired", snapshot.Version, "hufu-read-v1", snapshot.Identity)
                : _evaluator.Evaluate(snapshot, request, now);
            cancellationToken.ThrowIfCancellationRequested();
            if (decision is null || decision.SnapshotIdentity != snapshot.Identity || decision.SnapshotVersion != snapshot.Version ||
                !AuthorityValidation.ValidToken(decision.EvaluatorIdentity) || !AuthorityValidation.ValidToken(decision.ReasonCode) ||
                !Enum.IsDefined(decision.Status)) return new(request, AuthorityStatus.Unavailable);
            if (decision.Status == AuthorityStatus.Permit && _lifecycleGate is not null)
            {
                var binding = ExecutionLifecycleBinding.FromContext(request.Context);
                var lifecycle = await _lifecycleGate.CheckAsync(binding, now, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (lifecycle.Verdict == ExecutionLifecycleVerdict.Unavailable)
                    return new(request, AuthorityStatus.Unavailable, decision);
                if (lifecycle.Verdict == ExecutionLifecycleVerdict.Suspended)
                    decision = new AuthorityDecision(AuthorityStatus.Deny, "authority.lifecycle-suspended",
                        snapshot.Version, "hufu-lifecycle-v1", snapshot.Identity);
                else if (lifecycle.Verdict == ExecutionLifecycleVerdict.Terminal)
                    decision = new AuthorityDecision(AuthorityStatus.Deny, "authority.lifecycle-terminal",
                        snapshot.Version, "hufu-lifecycle-v1", snapshot.Identity);
            }
            var recorded = await _recorder.RecordAsync(request, decision, cancellationToken).AsTask()
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!recorded) return new(request, AuthorityStatus.Unavailable, decision);
            if (decision.Status == AuthorityStatus.Permit && !snapshot.HasUnchangedValidity(now, _clock.GetUtcNow()))
                return new(request, AuthorityStatus.Unavailable, decision, true);
            return new(request, decision.Status, decision, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return new(request, AuthorityStatus.Unavailable); }
    }
}
