using BiscuitSharp;

namespace Penghou.Hufu.Biscuit;

public enum BiscuitFailureCode
{
    None, InvalidAuthorityEnvelope, InvalidCredential, UnknownRootKey, AuthorityRevoked,
    AuthorityConstraintFailed, AuthorityDenied, WorkflowAuthorityDenied, PolicyDenied,
    AuthorizationBudgetExceeded, AuthorizationUnavailable, AuthorizationFailure, EnforcementPreconditionFailed,
}
public enum BiscuitComponentStatus { NotEvaluated, Permit, Deny, Unavailable }
public enum BiscuitRegistryStatus { Unavailable, Denied, Unknown, Revoked, Conflict, CapacityExceeded, Active, Registered, Replayed, Recorded }
public enum BiscuitRegistryOperation { Lookup, Register, Check, RecordVerification, Revoke, RetireKey }

/// <summary>Host-authenticated bindings; constructing this record alone confers no authority.</summary>
public sealed record BiscuitWorkloadBinding(AuthenticatedAuthorityContext Context, string Realm,
    string WorkflowId, string ActivityId, string Audience);
public sealed record BiscuitAuthenticatedWorkload(AuthorityStoreActor Actor, BiscuitWorkloadBinding Binding);
public sealed record BiscuitIssueRequest(AuthenticatedAuthorityContext Context, string LayerId, string GrantId);
public sealed record BiscuitIssuanceApproval(BiscuitAuthenticatedWorkload Workload, AuthoritySnapshot Snapshot,
    string LayerId, string GrantId, string GrantVersion);
public sealed record BiscuitResourceBinding(AuthorityRequest Request, string ProviderIdentity,
    string ObjectIdentity, string EffectIdentity, string? StartBindingIdentity = null);

/// <summary>
/// Required trusted host boundary. Authenticate actual callers; select immutable grant versions;
/// prove issuer/delegation ceilings and actual provider/effect bindings. No default permit gate ships.
/// </summary>
public interface IBiscuitAuthorityHost
{
    ValueTask<BiscuitAuthenticatedWorkload?> AuthenticateAsync(AuthenticatedAuthorityContext context, CancellationToken ct = default);
    ValueTask<BiscuitIssuanceApproval?> ApproveIssueAsync(BiscuitIssueRequest request, CancellationToken ct = default);
    ValueTask<bool> ApproveDerivationAsync(BiscuitAuthenticatedWorkload workload,
        BiscuitCredentialRegistration parent, BiscuitRestriction restriction, CancellationToken ct = default);
    ValueTask<BiscuitResourceBinding?> BindResourceAsync(BiscuitAuthenticatedWorkload workload,
        AuthorityRequest request, string? startBindingIdentity, CancellationToken ct = default);
}

/// <summary>A lease owns signing access for its lifetime; never exports the private key.</summary>
public interface IBiscuitSigningKeyLease : IDisposable
{
    string Realm { get; }
    string KeyId { get; }
    BiscuitPublicKey PublicKey { get; }
    BiscuitToken Build(BiscuitTokenBuilder builder);
}
public interface IBiscuitKeyProvider
{
    ValueTask<IBiscuitSigningKeyLease?> AcquireSigningKeyAsync(string realm, CancellationToken ct = default);
    ValueTask<BiscuitPublicKey?> FindVerificationKeyAsync(string realm, string keyId, CancellationToken ct = default);
}

/// <summary>Bounded owned bearer bytes. Explicit transport/export only; display is redacted.</summary>
public sealed class BiscuitEnvelope
{
    private readonly byte[] _token;
    public BiscuitEnvelope(string rootKeyId, byte[] tokenBytes, int version = 1, string profile = BiscuitProfile.Identity)
    {
        ArgumentNullException.ThrowIfNull(tokenBytes);
        if (tokenBytes.Length is < 1 or > BiscuitProfile.MaximumTokenBytes || !AuthorityValidation.ValidToken(rootKeyId) ||
            version != 1 || profile != BiscuitProfile.Identity) throw new ArgumentException("Invalid bounded Biscuit envelope.");
        RootKeyId = rootKeyId; Version = version; Profile = profile; _token = tokenBytes.ToArray();
    }
    public string RootKeyId { get; }
    public int Version { get; }
    public string Profile { get; }
    public int TokenLength => _token.Length;
    public byte[] GetTokenBytes() => _token.ToArray();
    public override string ToString() => "BiscuitEnvelope(hufu-biscuit-v1; credential redacted)";
}

/// <summary>Same-context restriction; no identity rebinding or arbitrary Datalog is accepted.</summary>
public sealed class BiscuitRestriction
{
    public BiscuitRestriction(IReadOnlyList<AuthorityAction> actions, AuthorityScope scope,
        IReadOnlyList<AuthorityScope> exclusions, DateTimeOffset notBefore, DateTimeOffset expiresAt)
    {
        var frozen = BiscuitProfile.FreezeGrant(new("restriction", actions, scope, exclusions, notBefore, expiresAt));
        Actions = frozen.Actions; Scope = frozen.Scope; Exclusions = frozen.Exclusions;
        NotBefore = frozen.NotBefore; ExpiresAt = frozen.ExpiresAt;
    }
    public IReadOnlyList<AuthorityAction> Actions { get; }
    public AuthorityScope Scope { get; }
    public IReadOnlyList<AuthorityScope> Exclusions { get; }
    public DateTimeOffset NotBefore { get; }
    public DateTimeOffset ExpiresAt { get; }
    internal AuthorityGrant Apply(string grantId) => new(grantId, Actions, Scope, Exclusions, NotBefore, ExpiresAt);
}

public sealed record BiscuitCredentialResult(BiscuitFailureCode FailureCode, BiscuitEnvelope? Envelope = null)
{
    public bool IsSuccess => FailureCode == BiscuitFailureCode.None && Envelope is not null;
}
public sealed record BiscuitVerificationResult(BiscuitFailureCode FailureCode, AuthorityDecision? Decision,
    string? VerificationId, BiscuitComponentStatus Biscuit, BiscuitComponentStatus CurrentAuthority,
    BiscuitComponentStatus Enforcement, BiscuitComponentStatus Evidence)
{
    public bool IsAuthorized => FailureCode == BiscuitFailureCode.None &&
        Decision?.Status == AuthorityStatus.Permit && VerificationId is not null &&
        Biscuit == BiscuitComponentStatus.Permit && CurrentAuthority == BiscuitComponentStatus.Permit &&
        Enforcement == BiscuitComponentStatus.Permit && Evidence == BiscuitComponentStatus.Permit;
}

/// <summary>Persist only bounded attribution and hashes, never bearer bytes or private keys.</summary>
public sealed record BiscuitDecisionEvidence(string Id, AuthorityStoreActor Actor, string Realm,
    string Fingerprint, string RootKeyId, AuthorityRequest Request, AuthorityDecision Decision,
    DateTimeOffset EvaluatedAt, DateTimeOffset ValidUntil, string EngineIdentity, string MappingIdentity,
    BiscuitResourceBinding? Resource, string EvidenceJson);

public interface IBiscuitDecisionRecorder
{
    ValueTask<bool> RecordAsync(BiscuitDecisionEvidence evidence, CancellationToken ct = default);
}
public sealed record BiscuitRegistryAccess(AuthorityStoreActor Actor, BiscuitRegistryOperation Operation,
    string Realm, AuthenticatedAuthorityContext? Context = null, string? Fingerprint = null,
    string? KeyId = null, BiscuitCredentialRegistration? Registration = null, BiscuitDecisionEvidence? Evidence = null, string? ReasonCode = null);
public interface IBiscuitRegistryAuthorizer
{
    ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(BiscuitRegistryAccess access, CancellationToken ct = default);
}
public sealed record BiscuitRegistrationLookup(BiscuitRegistryStatus Status, BiscuitCredentialRegistration? Registration = null, BiscuitDecisionEvidence? Evidence = null, string? ReasonCode = null);
public interface IBiscuitCredentialRegistry
{
    ValueTask<BiscuitRegistrationLookup> FindAsync(AuthorityStoreActor actor, string realm,
        AuthenticatedAuthorityContext context, string fingerprint, CancellationToken ct = default);
    ValueTask<BiscuitRegistryStatus> RegisterAsync(AuthorityStoreActor actor, BiscuitCredentialRegistration registration, CancellationToken ct = default);
    ValueTask<BiscuitRegistryStatus> CheckAsync(AuthorityStoreActor actor, BiscuitCredentialRegistration registration,
        DateTimeOffset now, CancellationToken ct = default);
}
