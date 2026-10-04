using System.Text.Json;

namespace Penghou.Hufu;

public enum AuthorityExplanationDetailLevel { Summary, Detailed }
public enum AuthorityExplanationReadStatus { Denied, Unavailable, Disclosed }

/// <summary>Host-authorized disclosure for the exact actor and immutable explanation.</summary>
public sealed record AuthorityExplanationAccess(AuthorityStatus Status, AuthorityStoreActor? Actor = null,
    string? ExplanationIdentity = null, AuthorityExplanationDetailLevel DetailLevel = AuthorityExplanationDetailLevel.Summary,
    DateTimeOffset? ValidUntil = null);

/// <summary>Authenticate the viewer and separately authorize explanation disclosure, independently of execution rights.</summary>
/// <remarks>Identifiers and explanation construction are not credentials. Use trusted host state, never echo caller fields as proof.</remarks>
public interface IAuthorityExplanationAccessPolicy
{
    ValueTask<AuthorityExplanationAccess?> AuthorizeAsync(AuthorityStoreActor presentedActor,
        AuthorityDecisionExplanation explanation, CancellationToken cancellationToken = default);
}

/// <summary>Explicitly authorized detail projection. No native objects, diagnostics or free-form reason text.</summary>
public sealed record AuthorityExplanationDetails(string Profile, string ExplanationIdentity,
    AuthorityRequest Request, string SnapshotIdentity, string SnapshotVersion, DateTimeOffset SnapshotValidUntil, string EvaluatorIdentity,
    DateTimeOffset EvaluatedAt, string? SchemaIdentity, string? EntityIdentity,
    AuthorityExplanationReason Reason, AuthorityExplanationCoverage Coverage,
    IReadOnlyList<AuthorityLayerExplanation> Layers, IReadOnlyList<AuthorityScope> MatchingMandatoryDenials);

/// <summary>A summary contains only the evaluator's outcome. Details require independent explicit authorization.</summary>
public sealed record AuthorityExplanationProjection(AuthorityStatus DecisionStatus,
    AuthorityExplanationDetails? Details = null);

/// <summary>Read authorization result only; this is never an execution authorization or evidence receipt.</summary>
public sealed record AuthorityExplanationRead(AuthorityExplanationReadStatus Status,
    AuthorityExplanationProjection? Projection = null);

/// <summary>Projects a host-authenticated evaluation capture through mandatory independent disclosure policy.</summary>
/// <remarks>
/// The host separately authorizes retrieval/custody of supplied records. This API
/// performs no historical lookup, authority evaluation, policy installation or resource access.
/// Its finite budget bounds asynchronous waits, not synchronous blocking or detached work.
/// </remarks>
public sealed class AuthorityExplanationReader
{
    public const int MaximumProjectionBytes = 1_048_576;
    private readonly IAuthorityExplanationAccessPolicy policy;
    private readonly TimeProvider clock;
    private readonly TimeSpan maximumEvaluationDuration;
    public AuthorityExplanationReader(IAuthorityExplanationAccessPolicy policy, TimeProvider? timeProvider = null,
        TimeSpan? maximumEvaluationDuration = null)
    {
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
        clock = timeProvider ?? TimeProvider.System;
        this.maximumEvaluationDuration = maximumEvaluationDuration ?? TimeSpan.FromSeconds(30);
        if (this.maximumEvaluationDuration <= TimeSpan.Zero || this.maximumEvaluationDuration > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(maximumEvaluationDuration));
    }
    public async ValueTask<AuthorityExplanationRead> ReadAsync(AuthorityStoreActor viewer,
        AuthorityDecisionExplanation explanation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (explanation is null || !AuthorityStoreValidation.ValidActor(viewer) || viewer.TenantId != explanation.Request.Context.TenantId)
            return new(AuthorityExplanationReadStatus.Denied);
        var startedAt = clock.GetUtcNow();
        using var budget = new CancellationTokenSource(maximumEvaluationDuration, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        try
        {
            var access = await policy.AuthorizeAsync(viewer, explanation, linked.Token).AsTask().WaitAsync(linked.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (access is null || !Enum.IsDefined(access.Status) || !WithinBudget()) return Unavailable();
            if (access.Status == AuthorityStatus.Deny) return new(AuthorityExplanationReadStatus.Denied);
            if (access.Status != AuthorityStatus.Permit || access.Actor != viewer ||
                access.ExplanationIdentity != explanation.Identity || !Enum.IsDefined(access.DetailLevel) ||
                access.ValidUntil is not { } validUntil || validUntil <= clock.GetUtcNow()) return Unavailable();
            var details = access.DetailLevel == AuthorityExplanationDetailLevel.Detailed
                ? new AuthorityExplanationDetails(AuthorityDecisionExplanation.Profile, explanation.Identity,
                    explanation.Request, explanation.Decision.SnapshotIdentity, explanation.Decision.SnapshotVersion, explanation.SnapshotValidUntil,
                    explanation.Decision.EvaluatorIdentity, explanation.EvaluatedAt, explanation.SchemaIdentity, explanation.EntityIdentity,
                    explanation.Reason, explanation.Coverage, explanation.Layers, explanation.MatchingMandatoryDenials)
                : null;
            var projection = new AuthorityExplanationProjection(explanation.Decision.Status, details);
            if (JsonSerializer.SerializeToUtf8Bytes(projection).Length > MaximumProjectionBytes ||
                !WithinBudget() || validUntil <= clock.GetUtcNow()) return Unavailable();
            cancellationToken.ThrowIfCancellationRequested();
            return new(AuthorityExplanationReadStatus.Disclosed, projection);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Unavailable(); }
        bool WithinBudget()
        {
            var now = clock.GetUtcNow();
            return !linked.IsCancellationRequested && now >= startedAt && now < startedAt.Add(maximumEvaluationDuration);
        }
    }
    private static AuthorityExplanationRead Unavailable() => new(AuthorityExplanationReadStatus.Unavailable);
}
