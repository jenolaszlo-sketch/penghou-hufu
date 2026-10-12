namespace Penghou.Hufu;

/// <summary>Current, attributable request preflight. This never authorizes protected dispatch by itself.</summary>
public sealed record AuthorityRequestAuthorization(AuthorityRequest Request, AuthorityStatus Status,
    AuthorityDecision? Decision = null, bool EvidenceRecorded = false)
{
    public bool IsAuthorized => Status == AuthorityStatus.Permit && Decision?.Status == AuthorityStatus.Permit &&
        EvidenceRecorded && AuthorityValidation.ValidToken(Decision.ReasonCode) &&
        AuthorityValidation.ValidToken(Decision.SnapshotVersion) && AuthorityValidation.ValidToken(Decision.EvaluatorIdentity) &&
        Decision.SnapshotIdentity is { Length: 64 } hash && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>
/// Trusted host composition for asynchronous current authority and mandatory evidence.
/// Return the exact request; a Permit without a valid decision and recorded evidence cannot authorize.
/// Resource enforcement and durable mutation start remain separate obligations.
/// </summary>
public interface IAuthorityRequestAuthorizer
{
    ValueTask<AuthorityRequestAuthorization> AuthorizeAsync(AuthorityRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Qualified derived-authority composition. A known derived grant cannot
/// return a usable Permit through this factory without the required
/// lineage/current-parent-policy checker: the checker is a mandatory
/// constructor argument, not an optional add-on.
/// </summary>
public static class DerivedAuthorityRequestAuthorizer
{
    public static CurrentAuthorityRequestAuthorizer Create(
        IAuthoritySnapshotSource source,
        IAuthorityEvaluator evaluator,
        IAuthorityDecisionRecorder recorder,
        IAuthorityDerivationStore derivations,
        TimeProvider? clock = null,
        IExecutionLifecycleGate? lifecycleGate = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(derivations);
        return new CurrentAuthorityRequestAuthorizer(source, evaluator, recorder, clock,
            new AuthorityDerivation.LineageAncestorLiveness(source, derivations),
            new AuthorityDerivation.LineageCurrentParentPolicy(source, derivations, evaluator),
            lifecycleGate);
    }
}

/// <summary>Composes the existing current snapshot, evaluator and required recorder without blocking asynchronous I/O.</summary>
public sealed class CurrentAuthorityRequestAuthorizer : IAuthorityRequestAuthorizer
{
    private readonly IAuthoritySnapshotSource _source;
    private readonly IAuthorityEvaluator _evaluator;
    private readonly IAuthorityDecisionRecorder _recorder;
    private readonly TimeProvider _clock;
    private readonly IAncestorLiveness? _ancestorLiveness;
    private readonly ICurrentParentPolicy? _parentPolicy;
    private readonly IExecutionLifecycleGate? _lifecycleGate;
    public CurrentAuthorityRequestAuthorizer(IAuthoritySnapshotSource source, IAuthorityEvaluator evaluator,
        IAuthorityDecisionRecorder recorder, TimeProvider? clock)
        : this(source, evaluator, recorder, clock, ancestorLiveness: null, parentPolicy: null, lifecycleGate: null)
    {
    }

    /// <summary>Composes the current snapshot, evaluator, and recorder with lineage checks for derived grants.</summary>
    /// <remarks>
    /// This is the qualified derived-authority composition when a current-parent
    /// checker is supplied: use <see cref="DerivedAuthorityRequestAuthorizer.Create"/>
    /// so the checker cannot be omitted. A null <paramref name="parentPolicy"/> is
    /// an incomplete composition: known derived grants deny rather than silently
    /// skipping the current-parent check. Root-only behavior is unchanged.
    /// </remarks>
    public CurrentAuthorityRequestAuthorizer(IAuthoritySnapshotSource source, IAuthorityEvaluator evaluator,
        IAuthorityDecisionRecorder recorder, TimeProvider? clock = null, IAncestorLiveness? ancestorLiveness = null,
        ICurrentParentPolicy? parentPolicy = null, IExecutionLifecycleGate? lifecycleGate = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _clock = clock ?? TimeProvider.System;
        _ancestorLiveness = ancestorLiveness;
        _parentPolicy = parentPolicy;
        _lifecycleGate = lifecycleGate;
    }

    private static bool HasDerivedGrants(AuthoritySnapshot snapshot) =>
        snapshot.Layers.SelectMany(layer => layer.Grants).Any(grant => grant.ParentGrantId is not null);

    public async ValueTask<AuthorityRequestAuthorization> AuthorizeAsync(AuthorityRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!AuthorityValidation.IsValidRequest(request)) return new(request, AuthorityStatus.Deny);
        try
        {
            var snapshot = await _source.GetCurrentAsync(request.Context, cancellationToken).AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot is null || snapshot.Context != request.Context) return new(request, AuthorityStatus.Unavailable);
            var now = _clock.GetUtcNow();
            var decision = snapshot.ValidUntil <= now ?
                new AuthorityDecision(AuthorityStatus.Deny, "authority.snapshot-expired", snapshot.Version, "hufu-read-v1", snapshot.Identity) :
                _evaluator.Evaluate(snapshot, request, now);
            cancellationToken.ThrowIfCancellationRequested();
            if (decision is null || decision.SnapshotIdentity != snapshot.Identity || decision.SnapshotVersion != snapshot.Version ||
                !AuthorityValidation.ValidToken(decision.EvaluatorIdentity) || !AuthorityValidation.ValidToken(decision.ReasonCode) ||
                !Enum.IsDefined(decision.Status)) return new(request, AuthorityStatus.Unavailable);
            // Ancestor liveness runs only on the way to Permit: existing denial
            // behavior is untouched, and root snapshots never pay for a lookup.
            if (decision.Status == AuthorityStatus.Permit && _ancestorLiveness is not null)
            {
                var liveness = await _ancestorLiveness.CheckAsync(snapshot, request, now, cancellationToken).ConfigureAwait(false);
                if (liveness != AncestorLivenessVerdict.Live)
                    decision = new AuthorityDecision(AuthorityStatus.Deny,
                        liveness == AncestorLivenessVerdict.AncestorRevoked ? "authority.ancestor-revoked" : "authority.lineage-unavailable",
                        snapshot.Version, "hufu-lineage-v1", snapshot.Identity);
            }
            // Current-parent policy runs only on the way to Permit. A known
            // derived grant reaching an incomplete composition (no qualified
            // checker) cannot Permit: omitting the checker is a fail-closed
            // Deny, never a downgrade to root-only evaluation.
            if (decision.Status == AuthorityStatus.Permit && HasDerivedGrants(snapshot))
            {
                if (_parentPolicy is null)
                {
                    decision = new AuthorityDecision(AuthorityStatus.Deny, "authority.derived-checker-missing",
                        snapshot.Version, "hufu-lineage-v1", snapshot.Identity);
                }
                else
                {
                    var policy = await _parentPolicy.CheckAsync(snapshot, request, now, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    switch (policy.Verdict)
                    {
                        case CurrentParentPolicyVerdict.Permit
                            when policy.ParentRequest is not null && policy.ParentDecision is not null:
                            // Mandatory parent evidence: without a recorded
                            // parent evaluation no usable Permit is returned.
                            var parentRecorded = await _recorder.RecordAsync(policy.ParentRequest, policy.ParentDecision, cancellationToken)
                                .AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!parentRecorded) return new(request, AuthorityStatus.Unavailable, decision);
                            break;
                        case CurrentParentPolicyVerdict.ParentDenied
                            when policy.ParentRequest is not null && policy.ParentDecision is not null:
                            // Best-effort parent-denial evidence; the child
                            // denial below is always recorded. Either way the
                            // operation stays denied.
                            await _recorder.RecordAsync(policy.ParentRequest, policy.ParentDecision, cancellationToken)
                                .AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
                            cancellationToken.ThrowIfCancellationRequested();
                            decision = new AuthorityDecision(AuthorityStatus.Deny, "authority.parent-policy-denied",
                                snapshot.Version, "hufu-lineage-v1", snapshot.Identity);
                            break;
                        case CurrentParentPolicyVerdict.ParentDenied:
                            return new(request, AuthorityStatus.Unavailable, decision);
                        case CurrentParentPolicyVerdict.AncestorRevoked:
                            decision = new AuthorityDecision(AuthorityStatus.Deny, "authority.ancestor-revoked",
                                snapshot.Version, "hufu-lineage-v1", snapshot.Identity);
                            break;
                        case CurrentParentPolicyVerdict.MalformedLineage:
                            decision = new AuthorityDecision(AuthorityStatus.Deny, "authority.lineage-malformed",
                                snapshot.Version, "hufu-lineage-v1", snapshot.Identity);
                            break;
                        case CurrentParentPolicyVerdict.UnsupportedLineage:
                            decision = new AuthorityDecision(AuthorityStatus.Deny, "authority.lineage-unsupported",
                                snapshot.Version, "hufu-lineage-v1", snapshot.Identity);
                            break;
                        case CurrentParentPolicyVerdict.LineageUnavailable:
                            decision = new AuthorityDecision(AuthorityStatus.Deny, "authority.lineage-unavailable",
                                snapshot.Version, "hufu-lineage-v1", snapshot.Identity);
                            break;
                        default:
                            return new(request, AuthorityStatus.Unavailable, decision);
                    }
                }
            }
            // Lifecycle gate applies only to derived grants and only when a
            // qualified lifecycle gate is composed. Absence of any lifecycle
            // fact is live; a store failure is unavailable, never a permit.
            if (decision.Status == AuthorityStatus.Permit && _lifecycleGate is not null && HasDerivedGrants(snapshot))
            {
                var binding = ExecutionLifecycleBinding.FromContext(request.Context);
                var verdict = await _lifecycleGate.CheckAsync(binding, now, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                switch (verdict)
                {
                    case ExecutionLifecycleVerdict.Live:
                        break;
                    case ExecutionLifecycleVerdict.Suspended:
                        decision = new AuthorityDecision(AuthorityStatus.Deny, "authority.lifecycle-suspended",
                            snapshot.Version, "hufu-lifecycle-v1", snapshot.Identity);
                        break;
                    case ExecutionLifecycleVerdict.Terminal:
                        decision = new AuthorityDecision(AuthorityStatus.Deny, "authority.lifecycle-terminal",
                            snapshot.Version, "hufu-lifecycle-v1", snapshot.Identity);
                        break;
                    default:
                        return new(request, AuthorityStatus.Unavailable, decision);
                }
            }
            var recorded = await _recorder.RecordAsync(request, decision, cancellationToken).AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!recorded) return new(request, AuthorityStatus.Unavailable, decision);
            // Snapshot and grant validity must survive required asynchronous recording.
            if (decision.Status == AuthorityStatus.Permit && !snapshot.HasUnchangedValidity(now, _clock.GetUtcNow()))
                return new(request, AuthorityStatus.Unavailable, decision, true);
            return new(request, decision.Status, decision, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return new(request, AuthorityStatus.Unavailable); }
    }
}
