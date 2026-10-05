using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BiscuitSharp;

namespace Penghou.Hufu.Biscuit;

public static class BiscuitProfile
{
    public const string Identity = "hufu-biscuit-v1";
    public const int MaximumTokenBytes = 65_536;
    public const int MaximumSourceBytes = 65_536;
    public const int MaximumBlocks = 32;
    public const int MaximumChecks = 256;
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    internal const string Policy = """
        allow if
          hufu_profile($p), request_profile($p),
          hufu_realm($realm), request_realm($realm),
          hufu_tenant($tenant), request_tenant($tenant),
          hufu_root_key($key), request_root_key($key),
          hufu_grant($grant), request_grant($grant),
          hufu_grant_version($version), request_grant_version($version),
          hufu_layer($layer), request_layer($layer),
          hufu_subject($subject), request_subject($subject),
          hufu_run($run), request_run($run),
          hufu_workflow($workflow), request_workflow($workflow),
          hufu_revision($revision), request_revision($revision),
          hufu_activity($activity), request_activity($activity),
          hufu_fence($fence), request_fence($fence),
          hufu_audience($audience), request_audience($audience),
          hufu_capability($capability), request_capability($capability),
          hufu_scope($scope), request_scope($scope),
          request_workspace($workspace), request_resource($path),
          scope_contains($scope, $workspace, $path)
          trusting authority;
        """;
    public static string MappingIdentity { get; } = "hufu-biscuit-mapping-v2:" + Hash(Utf8.GetBytes(
        string.Join("\n", Identity, "registered-typed-grants-v1", Policy,
            string.Join("\n", Enum.GetValues<AuthorityAction>().Select(action => action + "=" + Capability(action))))));
    /// <summary>Identity pinned by trusted host composition for operation-start evidence.</summary>
    public static string GetEvaluatorIdentity(string engineIdentity, string cedarEvaluatorIdentity)
    {
        if (!AuthorityValidation.ValidToken(engineIdentity) || !AuthorityValidation.ValidToken(cedarEvaluatorIdentity))
            throw new ArgumentException("Bounded engine and Cedar evaluator identities are required.");
        return "hufu-biscuit-v1:" + Hash(Utf8.GetBytes(string.Join("\n", MappingIdentity, engineIdentity, cedarEvaluatorIdentity)));
    }
    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static bool IsHash(string? value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static bool ValidBinding(BiscuitWorkloadBinding? binding) => binding is not null &&
        AuthorityValidation.ValidContext(binding.Context) && AuthorityValidation.ValidToken(binding.Realm) &&
        AuthorityValidation.ValidToken(binding.WorkflowId) && AuthorityValidation.ValidToken(binding.ActivityId) &&
        AuthorityValidation.ValidToken(binding.Audience);
    internal static bool ValidWorkload(BiscuitAuthenticatedWorkload? workload) => workload is not null &&
        ValidBinding(workload.Binding) && AuthorityStoreValidation.ValidActor(workload.Actor) &&
        workload.Actor.TenantId == workload.Binding.Context.TenantId;
    internal static AuthorityGrant FreezeGrant(AuthorityGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        // Reuse the core's bounded grant validation and defensive snapshots.
        return new AuthoritySnapshot(new("validation", "validation", "validation", "validation", "validation"),
            "validation", [new AuthorityLayer("validation", [grant])], [], grant.ExpiresAt).Layers[0].Grants[0];
    }
    internal static bool SameGrant(AuthorityGrant left, AuthorityGrant right) =>
        left.Id == right.Id && left.Scope == right.Scope && left.NotBefore == right.NotBefore &&
        left.ExpiresAt == right.ExpiresAt && left.Actions.Order().SequenceEqual(right.Actions.Order()) &&
        left.Exclusions.OrderBy(s => s.WorkspaceId, StringComparer.Ordinal)
            .ThenBy(s => s.RelativePath, StringComparer.Ordinal).ThenBy(s => s.Kind)
            .SequenceEqual(right.Exclusions.OrderBy(s => s.WorkspaceId, StringComparer.Ordinal)
                .ThenBy(s => s.RelativePath, StringComparer.Ordinal).ThenBy(s => s.Kind));
    internal static bool IsSubset(AuthorityGrant parent, AuthorityGrant child)
    {
        if (parent.Id != child.Id || child.Actions.Any(a => !parent.Actions.Contains(a)) ||
            child.NotBefore < parent.NotBefore || child.ExpiresAt > parent.ExpiresAt ||
            !AuthorityValidation.Contains(parent.Scope, child.Scope)) return false;
        foreach (var exclusion in parent.Exclusions)
        {
            AuthorityScope? intersection = AuthorityValidation.Contains(exclusion, child.Scope) ? child.Scope :
                AuthorityValidation.Contains(child.Scope, exclusion) ? exclusion : null;
            if (intersection is not null && !child.Exclusions.Any(e => AuthorityValidation.Contains(e, intersection))) return false;
        }
        return true;
    }
    internal static bool Covers(AuthorityGrant grant, AuthorityRequest request, DateTimeOffset now)
    {
        var target = new AuthorityScope(request.WorkspaceId, request.RelativePath, AuthorityScopeKind.Exact);
        return grant.NotBefore <= now && now < grant.ExpiresAt && grant.Actions.Contains(request.Action) &&
            AuthorityValidation.Contains(grant.Scope, target) &&
            !grant.Exclusions.Any(e => AuthorityValidation.Contains(e, target));
    }
    internal static string Capability(AuthorityAction action) => action switch
    {
        AuthorityAction.ReadFile => "fs.read", AuthorityAction.ListDirectory => "fs.list",
        AuthorityAction.ReadMetadata => "fs.metadata", AuthorityAction.PatchFile => "fs.patch",
        AuthorityAction.Release => "data.release", AuthorityAction.WriteFile => "fs.write",
        AuthorityAction.ExecuteProcess => "proc.execute",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };
    // Shared by production verification and the fixed-policy measurement probe.
    // Callers have already authenticated and validated registration and grant coverage.
    internal static BiscuitAuthorizer CreateAuthorizer(BiscuitToken token, BiscuitCredentialRegistration registration,
        IReadOnlyList<BiscuitCredentialRegistration> chain, AuthorityRequest request, DateTimeOffset evaluatedAt)
    {
        var authorizer = BiscuitAuthorizer.For(token);
        foreach (var pair in Bindings(registration))
            authorizer.AddFact("request_" + pair.Key + "(" + DatalogLiteral.String(pair.Value) + ");");
        authorizer.AddFact("request_capability(" + DatalogLiteral.String(Capability(request.Action)) + ");")
            .AddFact("request_workspace(" + DatalogLiteral.String(request.WorkspaceId) + ");")
            .AddFact("request_resource(" + DatalogLiteral.String(request.RelativePath) + ");")
            .AddTimeFact(evaluatedAt);
        foreach (var item in chain)
            authorizer.AddFact("scope_contains(" + DatalogLiteral.String(item.RestrictionId) + ", " +
                DatalogLiteral.String(request.WorkspaceId) + ", " + DatalogLiteral.String(request.RelativePath) + ");");
        return authorizer.AddPolicy(Policy);
    }
    internal static KeyValuePair<string, string>[] Bindings(BiscuitCredentialRegistration registration) =>
        Bindings(registration.Binding, registration.RootKeyId, registration.RootGrant.Id,
            registration.GrantVersion, registration.LayerId, registration.AuthorityScopeId);
    internal static KeyValuePair<string, string>[] Bindings(BiscuitWorkloadBinding binding, string keyId,
        string grantId, string grantVersion, string layerId, string scopeId) =>
    [
        new("profile", Identity), new("realm", binding.Realm), new("tenant", binding.Context.TenantId),
        new("root_key", keyId), new("grant", grantId), new("grant_version", grantVersion), new("layer", layerId),
        new("subject", binding.Context.SubjectId), new("run", binding.Context.RunId), new("workflow", binding.WorkflowId),
        new("revision", binding.Context.RevisionId), new("activity", binding.ActivityId), new("fence", binding.Context.FenceId),
        new("audience", binding.Audience), new("scope", scopeId),
    ];
    internal static string EngineIdentity()
    {
        var version = BiscuitEngine.GetVersion();
        if (version.AbiVersion != 1 || version.BiscuitAuthVersion != "6.0.0" || version.BridgeVersion != "0.1.0" ||
            version.UpstreamCommit != "0f0b4e0e6fe07220c1ba6b51bff21d450d94a975" ||
            !IsHash(version.NativeSha256) || !IsHash(version.CargoLockHash) ||
            version.RuntimeIdentifier is not ("win-x64" or "linux-x64" or "osx-arm64"))
            throw new BiscuitBridgeException("Unqualified Biscuit engine identity.");
        return "biscuit-engine-v1:" + Hash(Utf8.GetBytes(string.Join("\n", version.BiscuitAuthVersion,
            version.BridgeVersion, version.AbiVersion, version.RustVersion, version.TargetTriple,
            version.EnabledFeatures, version.NativeSha256, version.UpstreamCommit, version.CargoLockHash)));
    }
    internal static BiscuitFailureCode Classify(BiscuitAuthorizationResult result)
    {
        var failures = result.Errors.Where(e => e.Code == "evaluation_failure").ToArray();
        if (failures.Length != 0)
            return failures.All(e => e.EvaluationFailureReason is BiscuitEvaluationFailureReason.FactLimitExceeded or
                BiscuitEvaluationFailureReason.IterationLimitExceeded or BiscuitEvaluationFailureReason.TimeLimitExceeded)
                ? BiscuitFailureCode.AuthorizationBudgetExceeded : BiscuitFailureCode.AuthorizationFailure;
        if (result.Errors.Any(e => e.Code == "failed_check")) return BiscuitFailureCode.AuthorityConstraintFailed;
        return result.IsAuthorized ? BiscuitFailureCode.None : BiscuitFailureCode.AuthorityDenied;
    }
}
