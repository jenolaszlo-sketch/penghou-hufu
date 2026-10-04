using System.Security.Cryptography;
using System.Text;

namespace Penghou.Hufu;

public enum AuthorityExplanationReason
{
    Unknown, Permitted, LayerDenied, SnapshotExpired, InvalidRequest,
    EvaluatorUnavailable, EvaluatorIdentityMismatch, InvalidProjection,
    PolicyValidationFailed, RequestValidationFailed, PolicyEvaluationError
}
public enum AuthorityExplanationCoverage { SnapshotFactsOnly, CapturedLayerOutcomes }
public enum AuthorityGrantValidity { Active, NotYetValid, Expired }

/// <summary>Host-captured evaluator outcome for a layer; a null status means it was not evaluated.</summary>
public sealed record AuthorityLayerOutcome(string LayerId, AuthorityStatus? Status,
    string? PolicyIdentity = null, bool HadPolicyErrors = false);

/// <summary>Typed snapshot predicates, not a claim that this grant determined the evaluator's decision.</summary>
public sealed class AuthorityGrantExplanation
{
    internal AuthorityGrantExplanation(AuthorityGrant grant, AuthorityRequest request, DateTimeOffset now)
    {
        GrantId = grant.Id;
        Scope = grant.Scope;
        NotBefore = grant.NotBefore;
        ExpiresAt = grant.ExpiresAt;
        ActionMatches = grant.Actions.Contains(request.Action);
        ScopeMatches = AuthorityValidation.Contains(grant.Scope, new(request.WorkspaceId, request.RelativePath, AuthorityScopeKind.Exact));
        Validity = now < grant.NotBefore ? AuthorityGrantValidity.NotYetValid :
            now >= grant.ExpiresAt ? AuthorityGrantValidity.Expired : AuthorityGrantValidity.Active;
        MatchingExclusions = Array.AsReadOnly(grant.Exclusions.Where(scope =>
            AuthorityValidation.Contains(scope, new(request.WorkspaceId, request.RelativePath, AuthorityScopeKind.Exact))).ToArray());
    }
    public string GrantId { get; }
    public AuthorityScope Scope { get; }
    public DateTimeOffset NotBefore { get; }
    public DateTimeOffset ExpiresAt { get; }
    public bool ActionMatches { get; }
    public bool ScopeMatches { get; }
    public AuthorityGrantValidity Validity { get; }
    public IReadOnlyList<AuthorityScope> MatchingExclusions { get; }
}

public sealed class AuthorityLayerExplanation
{
    internal AuthorityLayerExplanation(AuthorityLayer layer, AuthorityLayerOutcome? outcome,
        AuthorityRequest request, DateTimeOffset now)
    {
        LayerId = layer.Id;
        Outcome = outcome;
        Grants = Array.AsReadOnly(layer.Grants.Select(grant => new AuthorityGrantExplanation(grant, request, now)).ToArray());
    }
    public string LayerId { get; }
    public AuthorityLayerOutcome? Outcome { get; }
    public IReadOnlyList<AuthorityGrantExplanation> Grants { get; }
}

/// <summary>Immutable trusted-host capture for one exact typed-path evaluation.</summary>
/// <remarks>
/// Construction confers no authority or proof of provenance. The host must authenticate
/// the supplied snapshot and actual evaluator capture. Do not serialize this host-only
/// object to callers; use AuthorityExplanationReader and its separate disclosure policy.
/// Snapshot predicates are explanatory facts, never a second policy decision or start receipt.
/// </remarks>
public sealed class AuthorityDecisionExplanation
{
    public const string Profile = "hufu-typed-path-explanation-v1";
    public AuthorityDecisionExplanation(AuthoritySnapshot snapshot, AuthorityRequest request,
        AuthorityDecision decision, DateTimeOffset evaluatedAt, IEnumerable<AuthorityLayerOutcome> layerOutcomes,
        string? schemaIdentity = null, string? entityIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(layerOutcomes);
        if (!AuthorityValidation.IsValidRequest(request) || request.Context != snapshot.Context ||
            decision is null || !Enum.IsDefined(decision.Status) ||
            decision.SnapshotIdentity != snapshot.Identity || decision.SnapshotVersion != snapshot.Version ||
            !AuthorityValidation.ValidToken(decision.EvaluatorIdentity) || !AuthorityValidation.ValidToken(decision.ReasonCode) ||
            !OptionalHash(schemaIdentity) || !OptionalHash(entityIdentity))
            throw new ArgumentException("An exact valid snapshot/request/evaluator capture is required.");
        var outcomes = layerOutcomes.Take(9).ToArray();
        if (outcomes.Length != 0 && outcomes.Length != snapshot.Layers.Count)
            throw new ArgumentException("Capture every layer in snapshot order, or supply no layer outcomes.");
        for (var i = 0; i < outcomes.Length; i++)
        {
            var outcome = outcomes[i];
            if (outcome is null || outcome.LayerId != snapshot.Layers[i].Id ||
                outcome.Status is { } status && !Enum.IsDefined(status) || !OptionalHash(outcome.PolicyIdentity) ||
                outcome.HadPolicyErrors && outcome.Status == AuthorityStatus.Permit)
                throw new ArgumentException("Invalid or inconsistent layer outcome.");
        }
        if (decision.Status == AuthorityStatus.Permit && outcomes.Any(o => o.Status != AuthorityStatus.Permit))
            throw new ArgumentException("A captured permit cannot contradict its layer outcomes.");
        Request = request;
        Decision = decision;
        SnapshotValidUntil = snapshot.ValidUntil;
        EvaluatedAt = evaluatedAt.ToUniversalTime();
        SchemaIdentity = schemaIdentity;
        EntityIdentity = entityIdentity;
        Reason = ClassifyReason(decision.ReasonCode);
        Coverage = outcomes.Length == snapshot.Layers.Count && outcomes.All(o => o.Status is not null)
            ? AuthorityExplanationCoverage.CapturedLayerOutcomes : AuthorityExplanationCoverage.SnapshotFactsOnly;
        Layers = Array.AsReadOnly(snapshot.Layers.Select((layer, i) => new AuthorityLayerExplanation(layer,
            outcomes.Length == 0 ? null : outcomes[i], request, EvaluatedAt)).ToArray());
        MatchingMandatoryDenials = Array.AsReadOnly(snapshot.MandatoryDenials.Where(scope =>
            AuthorityValidation.Contains(scope, new(request.WorkspaceId, request.RelativePath, AuthorityScopeKind.Exact))).ToArray());
        Identity = ComputeIdentity();
    }
    public string Identity { get; }
    public AuthorityRequest Request { get; }
    public AuthorityDecision Decision { get; }
    public DateTimeOffset SnapshotValidUntil { get; }
    public DateTimeOffset EvaluatedAt { get; }
    public string? SchemaIdentity { get; }
    public string? EntityIdentity { get; }
    public AuthorityExplanationReason Reason { get; }
    public AuthorityExplanationCoverage Coverage { get; }
    public IReadOnlyList<AuthorityLayerExplanation> Layers { get; }
    public IReadOnlyList<AuthorityScope> MatchingMandatoryDenials { get; }

    private string ComputeIdentity()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, AuthorityValidation.StrictUtf8, true);
        Text(Profile); Text(Decision.SnapshotIdentity); Text(Decision.SnapshotVersion);
        Text(Request.Context.TenantId); Text(Request.Context.SubjectId); Text(Request.Context.RunId);
        Text(Request.Context.RevisionId); Text(Request.Context.FenceId);
        writer.Write((int)Request.Action); Text(Request.WorkspaceId); Text(Request.RelativePath); Text(Request.RequestIdentity);
        writer.Write((int)Decision.Status); Text(Decision.ReasonCode); Text(Decision.EvaluatorIdentity);
        writer.Write(EvaluatedAt.UtcTicks); Text(SchemaIdentity ?? ""); Text(EntityIdentity ?? "");
        writer.Write(Layers.Count);
        foreach (var layer in Layers)
        {
            Text(layer.LayerId); writer.Write(layer.Outcome is not null);
            if (layer.Outcome is { } outcome)
            {
                writer.Write(outcome.Status is { } status ? (int)status : -1);
                Text(outcome.PolicyIdentity ?? ""); writer.Write(outcome.HadPolicyErrors);
            }
        }
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length))).ToLowerInvariant();
        void Text(string value)
        {
            var bytes = AuthorityValidation.StrictUtf8.GetBytes(value);
            writer.Write(bytes.Length); writer.Write(bytes);
        }
    }
    private static bool OptionalHash(string? value) => value is null ||
        value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static AuthorityExplanationReason ClassifyReason(string reason) => reason switch
    {
        "authority.permitted" => AuthorityExplanationReason.Permitted,
        "authority.layer-denied" => AuthorityExplanationReason.LayerDenied,
        "authority.snapshot-expired" => AuthorityExplanationReason.SnapshotExpired,
        "authority.invalid-request" => AuthorityExplanationReason.InvalidRequest,
        "cedar.native-unavailable" or "cedar.evaluation-failed" => AuthorityExplanationReason.EvaluatorUnavailable,
        "cedar.native-identity-mismatch" => AuthorityExplanationReason.EvaluatorIdentityMismatch,
        "cedar.projection-invalid" => AuthorityExplanationReason.InvalidProjection,
        "cedar.policy-validation-failed" => AuthorityExplanationReason.PolicyValidationFailed,
        "cedar.request-validation-failed" => AuthorityExplanationReason.RequestValidationFailed,
        "cedar.policy-evaluation-error" => AuthorityExplanationReason.PolicyEvaluationError,
        _ => AuthorityExplanationReason.Unknown
    };
}
