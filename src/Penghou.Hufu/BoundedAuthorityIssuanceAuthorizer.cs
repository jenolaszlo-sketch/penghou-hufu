namespace Penghou.Hufu;

/// <summary>Host-authenticated actor and, for publication, independently retrieved current issuer ceiling and exact approval facts.</summary>
/// <remarks>The host must authenticate the ceiling's provenance and issuer eligibility; copying the proposed snapshot is not proof.</remarks>
public sealed record AuthorityIssuancePrincipal(AuthorityStoreActor Actor,
    AuthoritySnapshot? IssuerCeiling = null, string? ApprovedSnapshotIdentity = null,
    long? ApprovedExpectedSequence = null, string? ApprovedCommandId = null,
    DateTimeOffset? ApprovalValidUntil = null);

/// <summary>Authenticate the presented session and resolve current issuer authority and approval from trusted host state.</summary>
/// <remarks>
/// Never authenticate by returning caller fields or use the proposal as its own ceiling.
/// Resolve the actor/session's current non-revoked issuer ceiling and approval independently.
/// Publication calls this boundary again
/// after operation policy evaluation. It is not an atomic parent-revocation/start fence.
/// </remarks>
public interface IAuthorityIssuanceTrustSource
{
    ValueTask<AuthorityIssuancePrincipal?> AuthenticateAsync(AuthorityStoreActor presentedActor,
        AuthorityStoreAccessRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Finite typed-scope publication policy composed with mandatory host operation policy.</summary>
/// <remarks>Requires fresh host authentication, exact approval and conservative containment in every issuer layer. Delegation is unsupported.</remarks>
public sealed class BoundedAuthorityIssuanceAuthorizer : IAuthorityStoreAuthorizer
{
    private readonly IAuthorityIssuanceTrustSource trust;
    private readonly IAuthorityStoreAuthorizer operationPolicy;
    private readonly TimeProvider clock;
    private readonly TimeSpan maximumEvaluationDuration;

    public BoundedAuthorityIssuanceAuthorizer(IAuthorityIssuanceTrustSource trust,
        IAuthorityStoreAuthorizer operationPolicy, TimeProvider? timeProvider = null,
        TimeSpan? maximumEvaluationDuration = null)
    {
        this.trust = trust ?? throw new ArgumentNullException(nameof(trust));
        this.operationPolicy = operationPolicy ?? throw new ArgumentNullException(nameof(operationPolicy));
        clock = timeProvider ?? TimeProvider.System;
        this.maximumEvaluationDuration = maximumEvaluationDuration ?? TimeSpan.FromSeconds(30);
        if (this.maximumEvaluationDuration <= TimeSpan.Zero || this.maximumEvaluationDuration > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(maximumEvaluationDuration));
    }

    public async ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(AuthorityStoreAccessRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null || !AuthorityStoreValidation.ValidActor(request.Actor) ||
            !AuthorityStoreValidation.ValidSubject(request.Subject) || request.Actor.TenantId != request.Subject.TenantId ||
            !Enum.IsDefined(request.Operation) ||
            (request.Context is not null && (!AuthorityValidation.ValidContext(request.Context) ||
                AuthoritySubject.From(request.Context) != request.Subject))) return Deny();
        if (request.Operation == AuthorityStoreOperation.Publish &&
            (request.ProposedSnapshot is null || request.Context != request.ProposedSnapshot.Context ||
                !AuthorityValidation.ValidToken(request.CommandId) || request.ExpectedSequence is null or < 0)) return Deny();

        var evaluatedAt = clock.GetUtcNow();
        using var budget = new CancellationTokenSource(maximumEvaluationDuration, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        var serviceToken = linked.Token;
        try
        {
            var principal = await trust.AuthenticateAsync(request.Actor, request, serviceToken).AsTask()
                .WaitAsync(serviceToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ValidPrincipal(principal, request) || !WithinBudget()) return Deny();
            if (request.Operation == AuthorityStoreOperation.Publish && !CanPublish(principal!, request, clock.GetUtcNow())) return Deny();

            var policy = await operationPolicy.AuthorizeAsync(request with { Actor = principal!.Actor }, serviceToken).AsTask()
                .WaitAsync(serviceToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (policy is null || policy.Status != AuthorityStatus.Permit || policy.Actor != principal.Actor || !WithinBudget()) return Deny();

            if (request.Operation == AuthorityStoreOperation.Publish)
            {
                // Policy may await external state. Reload current ceiling/approval rather than reuse the first capture.
                principal = await trust.AuthenticateAsync(request.Actor, request, serviceToken).AsTask()
                    .WaitAsync(serviceToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!ValidPrincipal(principal, request) || !WithinBudget() ||
                    !CanPublish(principal!, request, clock.GetUtcNow())) return Deny();
            }
            return new(AuthorityStatus.Permit, principal!.Actor);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Deny(); }

        bool WithinBudget()
        {
            var now = clock.GetUtcNow();
            return !serviceToken.IsCancellationRequested && now >= evaluatedAt && now < evaluatedAt.Add(maximumEvaluationDuration);
        }
    }

    private static AuthorityStoreAuthorization Deny() => new(AuthorityStatus.Deny);

    private static bool ValidPrincipal(AuthorityIssuancePrincipal? principal, AuthorityStoreAccessRequest request) =>
        principal is not null && AuthorityStoreValidation.ValidActor(principal.Actor) && principal.Actor == request.Actor;

    private static bool CanPublish(AuthorityIssuancePrincipal principal, AuthorityStoreAccessRequest request, DateTimeOffset now) =>
        principal.IssuerCeiling is { } ceiling && request.ProposedSnapshot is { } proposed &&
        ceiling.Identity != proposed.Identity && principal.ApprovedSnapshotIdentity == proposed.Identity && principal.ApprovedCommandId == request.CommandId &&
        principal.ApprovedExpectedSequence == request.ExpectedSequence && principal.ApprovalValidUntil is { } expires &&
        now < expires && Contained(proposed, ceiling, now);

    // Each proposed grant must fit one grant in every ceiling layer. Do not combine
    // layers, split scope coverage, or infer containment from a simulation.
    private static bool Contained(AuthoritySnapshot proposed, AuthoritySnapshot ceiling, DateTimeOffset now)
    {
        if (proposed.Context.TenantId != ceiling.Context.TenantId || proposed.ValidUntil <= now ||
            ceiling.ValidUntil <= now || proposed.ValidUntil > ceiling.ValidUntil) return false;
        foreach (var proposedLayer in proposed.Layers)
        foreach (var ceilingLayer in ceiling.Layers)
        foreach (var proposedGrant in proposedLayer.Grants)
        foreach (var action in proposedGrant.Actions)
        {
            if (!ceilingLayer.Grants.Any(grant => grant.Actions.Contains(action) &&
                grant.NotBefore <= now && now < grant.ExpiresAt &&
                grant.NotBefore <= proposedGrant.NotBefore && grant.ExpiresAt >= proposedGrant.ExpiresAt &&
                AuthorityValidation.Contains(grant.Scope, proposedGrant.Scope) &&
                !grant.Exclusions.Any(exclusion => Overlaps(exclusion, proposedGrant.Scope)))) return false;
        }
        foreach (var denial in ceiling.MandatoryDenials)
            if (!proposed.MandatoryDenials.Any(proposedDenial => AuthorityValidation.Contains(proposedDenial, denial))) return false;
        return true;
    }

    private static bool Overlaps(AuthorityScope left, AuthorityScope right) =>
        AuthorityValidation.Contains(left, right) || AuthorityValidation.Contains(right, left);
}
