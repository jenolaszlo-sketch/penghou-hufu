namespace Penghou.Hufu.Biscuit;

/// <summary>
/// The reconciled AE-3 composition. A genuine, registered, cryptographically
/// attenuated Biscuit credential gates entry, but the protected operation is
/// then authorized by the same AE-1 qualified current-authority path used by the
/// opaque-reference route (child authority, current parent policy, lifecycle,
/// optimistic read-set freshness, mandatory evidence).
/// <para>
/// Credential verification is not authorization: a signature never substitutes
/// for current Hufu authority, and core derivation lineage remains authoritative
/// over Biscuit block ancestry.
/// </para>
/// </summary>
public sealed class BiscuitQualifiedRequestAuthorizer : IAuthorityRequestAuthorizer
{
    private readonly BiscuitAuthorityService _service;
    private readonly BiscuitEnvelope _envelope;
    private readonly IAuthorityRequestAuthorizer _authority;

    public BiscuitQualifiedRequestAuthorizer(BiscuitAuthorityService service, BiscuitEnvelope envelope,
        IAuthorityRequestAuthorizer authority)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _envelope = envelope ?? throw new ArgumentNullException(nameof(envelope));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
    }

    public async ValueTask<AuthorityRequestAuthorization> AuthorizeAsync(AuthorityRequest request,
        CancellationToken cancellationToken = default)
    {
        var credential = await _service.VerifyCredentialAsync(_envelope, request, ct: cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!credential.IsVerified)
        {
            var status = credential.FailureCode is BiscuitFailureCode.AuthorizationUnavailable or BiscuitFailureCode.AuthorizationFailure
                ? AuthorityStatus.Unavailable : AuthorityStatus.Deny;
            return new(request, status);
        }
        return await _authority.AuthorizeAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
