namespace Penghou.Hufu;

/// <summary>
/// Exact host approval to derive one child grant. Binds the full derivation
/// tuple so an approval for one parent, delegation, generation, or authority
/// envelope can never authorize a materially different derivation. Distinct
/// from publication approval: "may publish snapshot X" and "may derive this
/// exact child from this parent" are different security statements.
/// </summary>
public sealed record DerivedAuthorityApproval(
    string ParentGrantId, string DelegationId, string Generation, string RequestedAuthorityHash);

/// <summary>Host-authenticated actor and, for publication or derivation, independently retrieved current approval facts.</summary>
/// <remarks>The host must authenticate the ceiling's provenance and issuer eligibility; copying the proposed snapshot is not proof.</remarks>
public sealed record AuthorityIssuancePrincipal(AuthorityStoreActor Actor,
    AuthoritySnapshot? IssuerCeiling = null, string? ApprovedSnapshotIdentity = null,
    long? ApprovedExpectedSequence = null, string? ApprovedCommandId = null,
    DateTimeOffset? ApprovalValidUntil = null, DerivedAuthorityApproval? ApprovedDerivation = null);

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

/// <summary>Finite typed-scope publication and derivation policy composed with mandatory host operation policy.</summary>
/// <remarks>
/// Requires fresh host authentication, exact approval and conservative containment in every issuer layer.
/// Derivation approval is exact-tuple and separate from publication approval.
/// The reload after operation policy is the freshness boundary: a fresh
/// authorization immediately preceding the protected operation authorizes that
/// operation to start. An issuance that has passed this gate may finish even if
/// approval changes immediately afterward; revocation blocks operations that
/// have not yet passed it.
/// </remarks>
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
        if (request.Operation == AuthorityStoreOperation.Derive &&
            (request.DerivationCommand is null || request.Context != request.DerivationCommand.ChildContext)) return Deny();

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
            if (request.Operation == AuthorityStoreOperation.Derive && !CanDerive(principal!, request, clock.GetUtcNow())) return Deny();

            var policy = await operationPolicy.AuthorizeAsync(request with { Actor = principal!.Actor }, serviceToken).AsTask()
                .WaitAsync(serviceToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (policy is null || policy.Status != AuthorityStatus.Permit || policy.Actor != principal.Actor || !WithinBudget()) return Deny();

            if (request.Operation is AuthorityStoreOperation.Publish or AuthorityStoreOperation.Derive)
            {
                // Policy may await external state. Reload current approval rather than reuse the first capture.
                principal = await trust.AuthenticateAsync(request.Actor, request, serviceToken).AsTask()
                    .WaitAsync(serviceToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!ValidPrincipal(principal, request) || !WithinBudget()) return Deny();
                if (request.Operation == AuthorityStoreOperation.Publish && !CanPublish(principal!, request, clock.GetUtcNow())) return Deny();
                if (request.Operation == AuthorityStoreOperation.Derive && !CanDerive(principal!, request, clock.GetUtcNow())) return Deny();
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

    // Exact-tuple derivation approval: parent, delegation, generation and the
    // canonical requested-authority hash must all match. Containment is proven
    // later by the store operation; this establishes permission to attempt it.
    private static bool CanDerive(AuthorityIssuancePrincipal principal, AuthorityStoreAccessRequest request, DateTimeOffset now)
    {
        if (principal.ApprovedDerivation is not { } approval || request.DerivationCommand is not { } command) return false;
        if (principal.ApprovalValidUntil is not { } expires || now >= expires) return false;
        return approval.ParentGrantId == command.ParentGrantId
            && approval.DelegationId == command.DelegationId
            && approval.Generation == command.Generation
            && approval.RequestedAuthorityHash == AuthorityDerivation.RequestedAuthorityHash(command.Requested);
    }

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
