namespace Penghou.Hufu.Biscuit;

/// <summary>
/// Binds one owned registered credential to asynchronous request preflight, including current Cedar
/// authority and mandatory evidence. Hosts select the service and credential; source code cannot.
/// Does not dispatch effects or bypass resource/start enforcement.
/// </summary>
public sealed class BiscuitRequestAuthorizer : IAuthorityRequestAuthorizer
{
    private readonly BiscuitAuthorityService _service;
    private readonly BiscuitEnvelope _envelope;
    public BiscuitRequestAuthorizer(BiscuitAuthorityService service, BiscuitEnvelope envelope)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _envelope = envelope ?? throw new ArgumentNullException(nameof(envelope));
    }
    public async ValueTask<AuthorityRequestAuthorization> AuthorizeAsync(AuthorityRequest request, CancellationToken cancellationToken = default)
    {
        var result = await _service.VerifyAsync(_envelope, request, ct: cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var status = result.IsAuthorized ? AuthorityStatus.Permit :
            result.FailureCode is BiscuitFailureCode.AuthorizationUnavailable or BiscuitFailureCode.AuthorizationFailure ?
            AuthorityStatus.Unavailable : AuthorityStatus.Deny;
        return new(request, status, result.Decision,
            result.VerificationId is not null && result.Evidence == BiscuitComponentStatus.Permit);
    }
}
