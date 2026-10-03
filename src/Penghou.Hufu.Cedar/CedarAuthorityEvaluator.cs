using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using CedarSharp;
using Penghou.Hufu;

namespace Penghou.Hufu.Cedar;

/// <summary>
/// Host-only diagnostic details for one Cedar projection. These objects retain
/// CedarSharp raw results and must not be serialized into agent-visible results.
/// </summary>
public sealed record CedarLayerEvaluationDetail(
    string LayerId,
    string SnapshotDigest,
    string SchemaDigest,
    string PolicyDigest,
    string EntityDigest,
    CedarValidationResult? PolicyValidation,
    CedarFullRequestCheckResult? RequestValidation,
    CedarAuthorizationResult? Authorization);

/// <summary>Trusted-host diagnostic view; the core AuthorityDecision remains redacted.</summary>
public sealed record CedarEvaluationDetails(
    AuthorityDecision Decision,
    CedarVersion? NativeIdentity,
    string SchemaDigest,
    string EntityDigest,
    IReadOnlyList<CedarLayerEvaluationDetail> Layers,
    string? FailureCode);

internal readonly record struct CedarAuthorizationClassification(AuthorityStatus Status, string ReasonCode);

/// <summary>
/// Projects immutable Hufu grants into a fixed Cedar schema and evaluates each
/// authority layer independently. CedarSharp is only the policy evaluator; it
/// does not authenticate contexts, issue grants, or enforce resource access.
/// </summary>
public sealed class CedarAuthorityEvaluator : IAuthorityEvaluator
{
    private const string SubjectType = "Hufu::Subject";
    private const string ResourceType = "Hufu::Resource";
    private const string ActionType = "Hufu::Action";
    private const string MappingVersion = "hufu-cedar-mapping-v1";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] ExpectedFeatures = ["datetime", "decimal", "ipaddr"];
    private static readonly HashSet<string> QualifiedTargets = new(StringComparer.Ordinal)
    {
        "x86_64-pc-windows-msvc", "x86_64-unknown-linux-gnu", "aarch64-apple-darwin"
    };
    private static readonly string SchemaText = """
        namespace Hufu {
          entity Subject;
          entity Resource in [Resource];
          action ReadFile appliesTo { principal: [Subject], resource: [Resource], context: {} };
          action ListDirectory appliesTo { principal: [Subject], resource: [Resource], context: {} };
          action ReadMetadata appliesTo { principal: [Subject], resource: [Resource], context: {} };
          action PatchFile appliesTo { principal: [Subject], resource: [Resource], context: {} };
          action Release appliesTo { principal: [Subject], resource: [Resource], context: {} };
          action WriteFile appliesTo { principal: [Subject], resource: [Resource], context: {} };
        }
        """;
    private static readonly CedarSchema Schema = CedarSchema.FromText(SchemaText);
    private static readonly string SchemaDigestValue = Digest(StrictUtf8.GetBytes(SchemaText));
    private static readonly AuthorityAction[] AllActions = Enum.GetValues<AuthorityAction>();
    private readonly CedarEngine _engine = new();

    public AuthorityDecision Evaluate(AuthoritySnapshot snapshot, AuthorityRequest request, DateTimeOffset now) =>
        EvaluateDetailed(snapshot, request, now).Decision;

    /// <summary>
    /// Evaluates a host-authenticated snapshot and retains full CedarSharp
    /// diagnostics for trusted host audit. Do not return this object to an agent.
    /// </summary>
    public CedarEvaluationDetails EvaluateDetailed(AuthoritySnapshot snapshot, AuthorityRequest request, DateTimeOffset now)
    {
        CedarVersion? native = null;
        var details = new List<CedarLayerEvaluationDetail>();
        var entitiesDigest = "";
        string? failure = null;
        try
        {
            if (snapshot is null || request is null || !AuthorityValidation.IsValidRequest(request) ||
                snapshot.Context != request.Context)
                return Finish(AuthorityStatus.Deny, "authority.invalid-request");
            var instant = now.ToUniversalTime();
            if (snapshot.ValidUntil <= instant)
                return Finish(AuthorityStatus.Deny, "authority.snapshot-expired");

            try { native = _engine.GetVersion(); }
            catch (CedarBridgeException) { failure = "cedar.native-unavailable"; return Finish(AuthorityStatus.Unavailable, failure); }
            if (!IsQualified(native))
            {
                failure = "cedar.native-identity-mismatch";
                return Finish(AuthorityStatus.Unavailable, failure);
            }

            var actionUid = ActionUid(request.Action);
            var subjectUid = SubjectUid(snapshot.Context);
            var (entities, entityDigest) = BuildEntities(snapshot, request, subjectUid);
            entitiesDigest = entityDigest;
            using var entitiesDocument = System.Text.Json.JsonDocument.Parse(CedarEntity.ToJson(entities));
            using var contextDocument = System.Text.Json.JsonDocument.Parse("{}");
            var frozenEntities = entitiesDocument.RootElement.Clone();
            var frozenContext = contextDocument.RootElement.Clone();

            var policySets = snapshot.Layers.Select(layer => BuildPolicies(snapshot, layer, instant)).ToArray();
            var policyDigests = policySets.Select(p => Digest(StrictUtf8.GetBytes(p.ToJsonString()))).ToArray();
            for (var i = 0; i < policySets.Length; i++)
                details.Add(new(snapshot.Layers[i].Id, snapshot.Identity, SchemaDigestValue,
                    policyDigests[i], entityDigest, null, null, null));
            var validations = new CedarValidationResult[snapshot.Layers.Count];
            for (var i = 0; i < policySets.Length; i++)
            {
                validations[i] = _engine.ValidatePolicies(policySets[i], Schema);
                details[i] = details[i] with { PolicyValidation = validations[i] };
                if (!validations[i].IsSuccess || !validations[i].IsValid)
                {
                    failure = "cedar.policy-validation-failed";
                    return Finish(AuthorityStatus.Deny, failure);
                }
            }

            var requestCheck = _engine.CheckFullRequest(subjectUid, actionUid,
                ResourceUid(snapshot.Context.TenantId, request.WorkspaceId, request.RelativePath),
                frozenContext, frozenEntities, Schema);
            if (!requestCheck.IsSuccess)
            {
                failure = "cedar.request-validation-failed";
                for (var i = 0; i < policySets.Length; i++)
                    details[i] = details[i] with { RequestValidation = requestCheck };
                return Finish(AuthorityStatus.Deny, failure);
            }
            for (var i = 0; i < policySets.Length; i++)
                details[i] = details[i] with { RequestValidation = requestCheck };

            var allPermit = true;
            var evaluationUnavailable = false;
            for (var i = 0; i < policySets.Length; i++)
            {
                var authRequest = new CedarAuthorizationRequest(subjectUid, actionUid,
                    ResourceUid(snapshot.Context.TenantId, request.WorkspaceId, request.RelativePath),
                    policySets[i], frozenContext, frozenEntities, Schema, validateRequest: true);
                var authorization = _engine.Authorize(authRequest);
                details[i] = details[i] with { Authorization = authorization };
                var classification = ClassifyAuthorization(authorization);
                if (classification.Status == AuthorityStatus.Unavailable)
                {
                    evaluationUnavailable = true;
                    failure ??= classification.ReasonCode;
                    allPermit = false;
                }
                else if (classification.Status != AuthorityStatus.Permit)
                {
                    failure ??= classification.ReasonCode;
                    allPermit = false;
                }
            }
            if (evaluationUnavailable) return Finish(AuthorityStatus.Unavailable, "cedar.evaluation-failed");
            if (failure is not null) return Finish(AuthorityStatus.Deny, failure);
            return Finish(allPermit ? AuthorityStatus.Permit : AuthorityStatus.Deny,
                allPermit ? "authority.permitted" : "authority.layer-denied");
        }
        catch (CedarBridgeException)
        {
            failure = "cedar.native-unavailable";
            return Finish(AuthorityStatus.Unavailable, failure);
        }
        catch
        {
            // Input/model/encoding faults fail closed without exposing exception text.
            failure = "cedar.projection-invalid";
            return Finish(AuthorityStatus.Deny, failure);
        }

        CedarEvaluationDetails Finish(AuthorityStatus status, string reason)
        {
            var evaluatorIdentity = EvaluatorIdentity(native);
            var decision = new AuthorityDecision(status, reason, snapshot?.Version ?? "",
                evaluatorIdentity, snapshot?.Identity ?? "");
            return new(decision, native, SchemaDigestValue, entitiesDigest,
                new ReadOnlyCollection<CedarLayerEvaluationDetail>(details.ToArray()), failure);
        }
    }

    private static bool IsQualified(CedarVersion? version) => version is not null && version.Features is not null &&
        version.AbiVersion == 1 && version.SdkVersion == "4.13.0" && version.LanguageVersion == "4.5" &&
        version.BridgeVersion == "0.1.0" && version.Features.Order(StringComparer.Ordinal).SequenceEqual(ExpectedFeatures, StringComparer.Ordinal) &&
        QualifiedTargets.Contains(version.Target) && IsSha256(version.Sha256);

    internal static CedarAuthorizationClassification ClassifyAuthorization(CedarAuthorizationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.IsSuccess)
            return new(AuthorityStatus.Unavailable, "cedar.evaluation-failed");
        if (result.PolicyErrors.Count != 0)
            return new(AuthorityStatus.Deny, "cedar.policy-evaluation-error");
        if (!result.IsCleanAllow)
            return new(AuthorityStatus.Deny, "authority.layer-denied");
        return new(AuthorityStatus.Permit, "authority.permitted");
    }

    private static string EvaluatorIdentity(CedarVersion? version)
    {
        var data = string.Join("\n", MappingVersion, SchemaDigestValue,
            version is null ? "cedar-unavailable" : version.AbiVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            version?.SdkVersion ?? "", version?.LanguageVersion ?? "", version?.BridgeVersion ?? "",
            version?.RustVersion ?? "", version?.Target ?? "",
            version is null || version.Features is null ? "" : string.Join(",", version.Features.Order(StringComparer.Ordinal)),
            version?.Sha256?.ToLowerInvariant() ?? "");
        return "hufu-cedar-v1:" + Digest(StrictUtf8.GetBytes(data));
    }

    private static CedarPolicySet BuildPolicies(AuthoritySnapshot snapshot, AuthorityLayer layer, DateTimeOffset now)
    {
        var policies = new Dictionary<string, string>(StringComparer.Ordinal);
        var subject = SubjectUid(snapshot.Context).ToString();
        foreach (var grant in layer.Grants)
        {
            if (now < grant.NotBefore || now >= grant.ExpiresAt) continue;
            var actions = grant.Actions.Order().Select(a => ActionUid(a).ToString()).ToArray();
            if (actions.Length == 0) continue;
            var conditions = grant.Exclusions.Select(exclusion => "!(" + ScopeExpression(snapshot.Context.TenantId, exclusion) + ")");
            var when = conditions.Any() ? " when { " + string.Join(" && ", conditions) + " }" : "";
            var scope = ScopeExpression(snapshot.Context.TenantId, grant.Scope);
            var id = PolicyId("permit", layer.Id, grant.Id);
            policies.Add(id, $"permit(principal == {subject}, action in [{string.Join(", ", actions)}], {scope}){when};");
        }
        foreach (var denial in snapshot.MandatoryDenials)
        {
            var actions = AllActions.Order().Select(a => ActionUid(a).ToString()).ToArray();
            var scope = ScopeExpression(snapshot.Context.TenantId, denial);
            var id = PolicyId("forbid", layer.Id, denial.WorkspaceId, denial.RelativePath, denial.Kind.ToString());
            policies.TryAdd(id, $"forbid(principal == {subject}, action in [{string.Join(", ", actions)}], {scope});");
        }
        return policies.Count == 0 ? CedarPolicySet.Empty : CedarPolicySet.FromPolicies(policies);
    }

    private static string ScopeExpression(string tenantId, AuthorityScope scope)
    {
        var uid = ResourceUid(tenantId, scope.WorkspaceId, scope.RelativePath).ToString();
        return scope.Kind == AuthorityScopeKind.Exact ? "resource == " + uid : "resource in " + uid;
    }

    private static (IReadOnlyList<CedarEntity> Entities, string Digest) BuildEntities(
        AuthoritySnapshot snapshot, AuthorityRequest request, CedarEntityUid subject)
    {
        var entities = new Dictionary<string, CedarEntity>(StringComparer.Ordinal);
        Add(new CedarEntity(subject));
        var previous = (CedarEntityUid?)null;
        var current = "";
        AddResource(snapshot.Context.TenantId, request.WorkspaceId, current, previous);
        previous = ResourceUid(snapshot.Context.TenantId, request.WorkspaceId, current);
        if (request.RelativePath.Length != 0)
        {
            foreach (var segment in request.RelativePath.Split('/'))
            {
                current = current.Length == 0 ? segment : current + "/" + segment;
                var next = ResourceUid(snapshot.Context.TenantId, request.WorkspaceId, current);
                AddResource(snapshot.Context.TenantId, request.WorkspaceId, current, previous);
                previous = next;
            }
        }
        foreach (var layer in snapshot.Layers)
        foreach (var grant in layer.Grants)
        {
            AddAnchor(grant.Scope);
            foreach (var exclusion in grant.Exclusions) AddAnchor(exclusion);
        }
        foreach (var denial in snapshot.MandatoryDenials) AddAnchor(denial);
        var list = Array.AsReadOnly(entities.Values.ToArray());
        return (list, Digest(StrictUtf8.GetBytes(CedarEntity.ToJson(list))));

        void AddResource(string tenant, string workspace, string path, CedarEntityUid? parent)
        {
            var uid = ResourceUid(tenant, workspace, path);
            Add(new CedarEntity(uid, "{}", parent is null ? null : [parent]));
        }
        void AddAnchor(AuthorityScope scope)
        {
            var uid = ResourceUid(snapshot.Context.TenantId, scope.WorkspaceId, scope.RelativePath);
            var key = UidKey(uid);
            if (!entities.ContainsKey(key)) Add(new CedarEntity(uid));
        }
        void Add(CedarEntity entity) => entities.TryAdd(UidKey(entity.Uid), entity);
    }

    private static CedarEntityUid SubjectUid(AuthenticatedAuthorityContext context) => new(SubjectType,
        HashId("hufu-subject-v1", context.TenantId, context.SubjectId, context.RunId, context.RevisionId, context.FenceId));

    private static CedarEntityUid ResourceUid(string tenant, string workspace, string path) => new(ResourceType,
        HashId("hufu-resource-v1", tenant, workspace, path));

    private static CedarEntityUid ActionUid(AuthorityAction action) => new(ActionType, action switch
    {
        AuthorityAction.ReadFile => "ReadFile",
        AuthorityAction.ListDirectory => "ListDirectory",
        AuthorityAction.ReadMetadata => "ReadMetadata",
        AuthorityAction.PatchFile => "PatchFile",
        AuthorityAction.Release => "Release",
        AuthorityAction.WriteFile => "WriteFile",
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    });

    private static string UidKey(CedarEntityUid uid) => uid.Type + "\0" + uid.Id;

    private static string PolicyId(params string[] fields) => "p" + HashId("hufu-policy-id-v1", fields);

    private static string HashId(string domain, params string[] fields)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, StrictUtf8, leaveOpen: true);
        Frame(writer, domain);
        foreach (var field in fields) Frame(writer, field);
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)))).ToLowerInvariant();
    }

    private static void Frame(BinaryWriter writer, string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        writer.Write(bytes.Length); // Canonical Int32 little-endian UTF-8 length frame.
        writer.Write(bytes);
    }

    private static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
