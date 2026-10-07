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

/// <summary>Composes the existing current snapshot, evaluator and required recorder without blocking asynchronous I/O.</summary>
public sealed class CurrentAuthorityRequestAuthorizer : IAuthorityRequestAuthorizer
{
    private readonly IAuthoritySnapshotSource _source;
    private readonly IAuthorityEvaluator _evaluator;
    private readonly IAuthorityDecisionRecorder _recorder;
    private readonly TimeProvider _clock;
    private readonly IAncestorLiveness? _ancestorLiveness;
    public CurrentAuthorityRequestAuthorizer(IAuthoritySnapshotSource source, IAuthorityEvaluator evaluator,
        IAuthorityDecisionRecorder recorder, TimeProvider? clock = null, IAncestorLiveness? ancestorLiveness = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _clock = clock ?? TimeProvider.System;
        _ancestorLiveness = ancestorLiveness;
    }

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
