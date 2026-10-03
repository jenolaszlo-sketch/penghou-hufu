using System.Text.Json;
using BiscuitSharp;
using Penghou.Hufu.Cedar;

namespace Penghou.Hufu.Biscuit;

/// <summary>
/// Optional registered online profile. Verification is an evidenced preflight, never protected dispatch.
/// Host authentication, current Hufu/Cedar authority and required durable evidence are mandatory.
/// </summary>
public sealed class BiscuitAuthorityService
{
    private readonly IBiscuitAuthorityHost _host;
    private readonly IBiscuitKeyProvider _keys;
    private readonly IBiscuitCredentialRegistry _registry;
    private readonly IAuthoritySnapshotSource _snapshots;
    private readonly CedarAuthorityEvaluator _cedar;
    private readonly IBiscuitDecisionRecorder _recorder;
    private readonly BiscuitAuthorizerLimits _limits;
    private readonly TimeProvider _clock;
    public BiscuitAuthorityService(IBiscuitAuthorityHost host, IBiscuitKeyProvider keys,
        IBiscuitCredentialRegistry registry, IAuthoritySnapshotSource snapshots, CedarAuthorityEvaluator cedar,
        IBiscuitDecisionRecorder recorder, BiscuitAuthorizerLimits limits, TimeProvider? clock = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _cedar = cedar ?? throw new ArgumentNullException(nameof(cedar));
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits)); _clock = clock ?? TimeProvider.System;
        if (limits.MaxFacts == 0 || limits.MaxIterations == 0 || limits.MaxTime < TimeSpan.FromMilliseconds(1) ||
            limits.MaxTime.Ticks % TimeSpan.TicksPerMillisecond != 0) throw new ArgumentException("Positive integral-millisecond host budgets are required.");
        EngineIdentity = BiscuitProfile.EngineIdentity();
    }
    public string EngineIdentity { get; }

    public async ValueTask<BiscuitCredentialResult> IssueAsync(BiscuitIssueRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (request is null || !AuthorityValidation.ValidContext(request.Context) ||
            !AuthorityValidation.ValidToken(request.LayerId) || !AuthorityValidation.ValidToken(request.GrantId))
            return new(BiscuitFailureCode.WorkflowAuthorityDenied);
        try
        {
            var approval = await _host.ApproveIssueAsync(request, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (approval is null || !BiscuitProfile.ValidWorkload(approval.Workload) ||
                approval.Workload.Binding.Context != request.Context || approval.Snapshot is null ||
                approval.Snapshot.Context != request.Context || approval.LayerId != request.LayerId ||
                approval.GrantId != request.GrantId || !AuthorityValidation.ValidToken(approval.GrantVersion))
                return new(BiscuitFailureCode.WorkflowAuthorityDenied);
            var current = await _snapshots.GetCurrentAsync(request.Context, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (current is null) return new(BiscuitFailureCode.AuthorizationUnavailable);
            var grant = current.Layers.SingleOrDefault(l => l.Id == request.LayerId)?.Grants.SingleOrDefault(g => g.Id == request.GrantId);
            var now = _clock.GetUtcNow();
            if (current.Identity != approval.Snapshot.Identity || current.Context != request.Context ||
                current.ValidUntil <= now || grant is null || grant.NotBefore > now || grant.ExpiresAt <= now)
                return new(BiscuitFailureCode.WorkflowAuthorityDenied);
            using var lease = await _keys.AcquireSigningKeyAsync(approval.Workload.Binding.Realm, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (lease is null) return new(BiscuitFailureCode.UnknownRootKey);
            if (lease.Realm != approval.Workload.Binding.Realm || !AuthorityValidation.ValidToken(lease.KeyId) ||
                lease.PublicKey.Algorithm != BiscuitKeyAlgorithm.Ed25519) return new(BiscuitFailureCode.AuthorizationFailure);
            var scopeId = Guid.NewGuid().ToString("N");
            var builder = BiscuitTokenBuilder.Create();
            foreach (var pair in BiscuitProfile.Bindings(approval.Workload.Binding, lease.KeyId, grant.Id,
                approval.GrantVersion, request.LayerId, scopeId))
                builder.AddFact("hufu_" + pair.Key + "({value})", [KeyValuePair.Create("value", BiscuitParam.Str(pair.Value))]);
            foreach (var action in grant.Actions)
                builder.AddFact("hufu_capability({value})", [KeyValuePair.Create("value", BiscuitParam.Str(BiscuitProfile.Capability(action)))]);
            var token = lease.Build(builder);
            ct.ThrowIfCancellationRequested();
            var registration = RegisterShape(token, approval.Workload.Binding, lease.KeyId, approval.Workload.Actor.ActorId,
                request.LayerId, approval.GrantVersion, scopeId, scopeId, grant, grant, null, 0);
            if (_clock.GetUtcNow() >= current.ValidUntil || _clock.GetUtcNow() >= grant.ExpiresAt)
                return new(BiscuitFailureCode.WorkflowAuthorityDenied);
            var written = await _registry.RegisterAsync(approval.Workload.Actor, registration, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (written is not (BiscuitRegistryStatus.Registered or BiscuitRegistryStatus.Replayed)) return new(MapRegistry(written));
            return new(BiscuitFailureCode.None, new(lease.KeyId, token.ToBytes()));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (BiscuitBridgeException) { return new(BiscuitFailureCode.AuthorizationFailure); }
        catch { return new(BiscuitFailureCode.AuthorizationFailure); }
    }

    public async ValueTask<BiscuitCredentialResult> AttenuateAsync(AuthenticatedAuthorityContext context,
        BiscuitEnvelope parentEnvelope, BiscuitRestriction restriction, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!AuthorityValidation.ValidContext(context) || parentEnvelope is null || restriction is null)
            return new(BiscuitFailureCode.InvalidAuthorityEnvelope);
        try
        {
            var workload = await _host.AuthenticateAsync(context, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!BiscuitProfile.ValidWorkload(workload) || workload!.Binding.Context != context) return new(BiscuitFailureCode.WorkflowAuthorityDenied);
            var loaded = await LoadAsync(workload, parentEnvelope, ct).ConfigureAwait(false);
            if (loaded.Failure != BiscuitFailureCode.None) return new(loaded.Failure);
            var parent = loaded.Registration!;
            var childGrant = restriction.Apply(parent.RootGrant.Id);
            if (parent.BlockCount >= BiscuitProfile.MaximumBlocks || parent.CheckCount + 2 > BiscuitProfile.MaximumChecks ||
                !BiscuitProfile.IsSubset(parent.EffectiveGrant, childGrant)) return new(BiscuitFailureCode.AuthorityConstraintFailed);
            var current = await _snapshots.GetCurrentAsync(context, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (current is null) return new(BiscuitFailureCode.AuthorizationUnavailable);
            if (!Applicable(current, parent, _clock.GetUtcNow())) return new(BiscuitFailureCode.WorkflowAuthorityDenied);
            if (!await _host.ApproveDerivationAsync(workload, parent, restriction, ct).ConfigureAwait(false))
                return new(BiscuitFailureCode.AuthorityDenied);
            ct.ThrowIfCancellationRequested();
            var restrictionId = Guid.NewGuid().ToString("N");
            var checks = "check if " + string.Join(" or ", childGrant.Actions.Select(a =>
                "request_capability(" + DatalogLiteral.String(BiscuitProfile.Capability(a)) + ")")) + ";\n" +
                "check if request_workspace($workspace), request_resource($path), scope_contains(" +
                DatalogLiteral.String(restrictionId) + ", $workspace, $path);";
            var child = loaded.Token!.Attenuate(BiscuitBlock.Create(checks));
            ct.ThrowIfCancellationRequested();
            var registration = RegisterShape(child, parent.Binding, parent.RootKeyId, parent.IssuerId,
                parent.LayerId, parent.GrantVersion, parent.AuthorityScopeId, restrictionId, parent.RootGrant,
                childGrant, parent.Fingerprint, parent.CheckCount + 2);
            var written = await _registry.RegisterAsync(workload.Actor, registration, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return written is BiscuitRegistryStatus.Registered or BiscuitRegistryStatus.Replayed
                ? new(BiscuitFailureCode.None, new(parent.RootKeyId, child.ToBytes())) : new(MapRegistry(written));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (BiscuitSealedTokenException) { return new(BiscuitFailureCode.AuthorityConstraintFailed); }
        catch (BiscuitBridgeException) { return new(BiscuitFailureCode.AuthorizationFailure); }
        catch { return new(BiscuitFailureCode.AuthorizationFailure); }
    }

    public async ValueTask<BiscuitVerificationResult> VerifyAsync(BiscuitEnvelope envelope, AuthorityRequest request,
        string? startBindingIdentity = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var biscuit = BiscuitComponentStatus.NotEvaluated; var authority = BiscuitComponentStatus.NotEvaluated;
        var enforcement = BiscuitComponentStatus.NotEvaluated;
        if (envelope is null || !AuthorityValidation.IsValidRequest(request) ||
            startBindingIdentity is not null && !BiscuitProfile.IsHash(startBindingIdentity))
            return Fail(BiscuitFailureCode.InvalidAuthorityEnvelope);
        try
        {
            var workload = await _host.AuthenticateAsync(request.Context, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!BiscuitProfile.ValidWorkload(workload) || workload!.Binding.Context != request.Context)
                return Fail(BiscuitFailureCode.WorkflowAuthorityDenied);
            var loaded = await LoadAsync(workload, envelope, ct).ConfigureAwait(false);
            if (loaded.Failure != BiscuitFailureCode.None) return Fail(loaded.Failure);
            var registration = loaded.Registration!;
            var current = await _snapshots.GetCurrentAsync(request.Context, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (current is null) return Fail(BiscuitFailureCode.AuthorizationUnavailable);
            var evaluatedAt = _clock.GetUtcNow();
            if (!Applicable(current, registration, evaluatedAt)) return Fail(BiscuitFailureCode.WorkflowAuthorityDenied);

            // Preserve the actual all-layer Cedar projection/evaluation; never flatten grants.
            var details = _cedar.EvaluateDetailed(current, request, evaluatedAt);
            ct.ThrowIfCancellationRequested();
            if (details.Decision.SnapshotIdentity != current.Identity || details.Decision.SnapshotVersion != current.Version ||
                !AuthorityValidation.ValidToken(details.Decision.EvaluatorIdentity) || !Enum.IsDefined(details.Decision.Status))
                return Fail(BiscuitFailureCode.AuthorizationFailure);
            authority = details.Decision.Status switch
            {
                AuthorityStatus.Permit => BiscuitComponentStatus.Permit,
                AuthorityStatus.Deny => BiscuitComponentStatus.Deny,
                _ => BiscuitComponentStatus.Unavailable,
            };
            BiscuitResourceBinding? resource = null;
            BiscuitAuthorizationResult? nativeResult = null;
            if (authority != BiscuitComponentStatus.Permit)
                return await Finish(details.Decision.Status == AuthorityStatus.Deny ? BiscuitFailureCode.PolicyDenied :
                    BiscuitFailureCode.AuthorizationUnavailable).ConfigureAwait(false);
            if (details.Layers.Count != current.Layers.Count || details.Layers.Any(l => l.Authorization?.IsCleanAllow != true))
                return await Finish(BiscuitFailureCode.AuthorizationFailure).ConfigureAwait(false);
            if (!BiscuitProfile.Covers(registration.EffectiveGrant, request, evaluatedAt))
                return await Finish(BiscuitFailureCode.AuthorityConstraintFailed).ConfigureAwait(false);
            resource = await _host.BindResourceAsync(workload, request, startBindingIdentity, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (resource is null || resource.Request != request ||
                !AuthorityValidation.ValidToken(resource.ProviderIdentity) || !AuthorityValidation.ValidToken(resource.ObjectIdentity) ||
                !AuthorityValidation.ValidToken(resource.EffectIdentity) || resource.StartBindingIdentity != startBindingIdentity)
            {
                enforcement = BiscuitComponentStatus.Deny;
                return await Finish(BiscuitFailureCode.EnforcementPreconditionFailed).ConfigureAwait(false);
            }
            enforcement = BiscuitComponentStatus.Permit;
            var ancestry = await LoadChainAsync(workload, registration, ct).ConfigureAwait(false);
            if (ancestry.Failure != BiscuitFailureCode.None) return await Finish(ancestry.Failure).ConfigureAwait(false);
            var chain = ancestry.Chain!;
            if (chain.Any(item => !BiscuitProfile.Covers(item.EffectiveGrant, request, evaluatedAt)))
                return await Finish(BiscuitFailureCode.AuthorityConstraintFailed).ConfigureAwait(false);
            nativeResult = BiscuitProfile.CreateAuthorizer(loaded.Token!, registration, chain, request, evaluatedAt)
                .WithLimits(_limits).Authorize();
            ct.ThrowIfCancellationRequested();
            var failure = BiscuitProfile.Classify(nativeResult);
            biscuit = failure == BiscuitFailureCode.None ? BiscuitComponentStatus.Permit : BiscuitComponentStatus.Deny;
            return await Finish(failure).ConfigureAwait(false);

            async ValueTask<BiscuitVerificationResult> Finish(BiscuitFailureCode code)
            {
                var validUntil = registration.EffectiveGrant.ExpiresAt < current.ValidUntil ?
                    registration.EffectiveGrant.ExpiresAt : current.ValidUntil;
                if (code == BiscuitFailureCode.None && (_clock.GetUtcNow() >= validUntil || !current.HasUnchangedValidity(evaluatedAt, _clock.GetUtcNow())))
                    code = BiscuitFailureCode.WorkflowAuthorityDenied;
                var status = code == BiscuitFailureCode.None ? AuthorityStatus.Permit :
                    code == BiscuitFailureCode.AuthorizationUnavailable ? AuthorityStatus.Unavailable : AuthorityStatus.Deny;
                var evaluatorId = BiscuitProfile.GetEvaluatorIdentity(EngineIdentity, details.Decision.EvaluatorIdentity);
                var decision = new AuthorityDecision(status, code.ToString(), current.Version, evaluatorId, current.Identity);
                var evidence = new BiscuitDecisionEvidence(Guid.NewGuid().ToString("N"), workload.Actor, workload.Binding.Realm,
                    registration.Fingerprint, registration.RootKeyId, request, decision, evaluatedAt, validUntil,
                    EngineIdentity, BiscuitProfile.MappingIdentity, resource, EvidenceJson(details, nativeResult, code));
                bool recorded = await _recorder.RecordAsync(evidence, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (!recorded || code == BiscuitFailureCode.None && (_clock.GetUtcNow() >= validUntil || !current.HasUnchangedValidity(evaluatedAt, _clock.GetUtcNow())))
                    return new(code == BiscuitFailureCode.None ? BiscuitFailureCode.AuthorizationUnavailable : code,
                        decision with { Status = code == BiscuitFailureCode.None ? AuthorityStatus.Unavailable : status, ReasonCode = code == BiscuitFailureCode.None ? BiscuitFailureCode.AuthorizationUnavailable.ToString() : code.ToString() },
                        null, biscuit, authority, enforcement, BiscuitComponentStatus.Unavailable);
                return new(code, decision, evidence.Id, biscuit, authority, enforcement, BiscuitComponentStatus.Permit);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (BiscuitBridgeException) { return Fail(BiscuitFailureCode.AuthorizationFailure); }
        catch (BiscuitTokenException) { return Fail(BiscuitFailureCode.InvalidCredential); }
        catch { return Fail(BiscuitFailureCode.AuthorizationFailure); }

        BiscuitVerificationResult Fail(BiscuitFailureCode code) =>
            new(code, null, null, biscuit, authority, enforcement, BiscuitComponentStatus.NotEvaluated);
    }

    private async ValueTask<(BiscuitFailureCode Failure, BiscuitToken? Token, BiscuitCredentialRegistration? Registration)>
        LoadAsync(BiscuitAuthenticatedWorkload workload, BiscuitEnvelope envelope, CancellationToken ct)
    {
        var publicKey = await _keys.FindVerificationKeyAsync(workload.Binding.Realm, envelope.RootKeyId, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (publicKey is null) return (BiscuitFailureCode.UnknownRootKey, null, null);
        if (publicKey.Algorithm != BiscuitKeyAlgorithm.Ed25519) return (BiscuitFailureCode.AuthorizationFailure, null, null);
        var received = envelope.GetTokenBytes();
        BiscuitToken token;
        try { token = BiscuitToken.Parse(received, publicKey); }
        catch (BiscuitTokenException) { return (BiscuitFailureCode.InvalidCredential, null, null); }
        ct.ThrowIfCancellationRequested();
        if (!received.AsSpan().SequenceEqual(token.ToBytes())) return (BiscuitFailureCode.InvalidCredential, null, null);
        var fingerprint = BiscuitProfile.Hash(received);
        var lookup = await _registry.FindAsync(workload.Actor, workload.Binding.Realm, workload.Binding.Context, fingerprint, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var registration = lookup.Registration;
        if (lookup.Status != BiscuitRegistryStatus.Active || registration is null) return (MapRegistry(lookup.Status), null, null);
        var inspection = token.Inspect();
        ct.ThrowIfCancellationRequested();
        if (registration.Fingerprint != fingerprint || registration.Binding != workload.Binding ||
            registration.RootKeyId != envelope.RootKeyId || registration.TokenLength != received.Length ||
            registration.BlockCount != inspection.BlockCount || inspection.RootKeyAlgorithm != BiscuitKeyAlgorithm.Ed25519 ||
            !registration.RevocationIds.SequenceEqual(token.GetRevocationIds().Select(id => Convert.ToHexString(id.Value).ToLowerInvariant())))
            return (BiscuitFailureCode.InvalidCredential, null, null);
        var active = await _registry.CheckAsync(workload.Actor, registration, _clock.GetUtcNow(), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return active == BiscuitRegistryStatus.Active ? (BiscuitFailureCode.None, token, registration) : (MapRegistry(active), null, null);
    }

    private async ValueTask<(BiscuitFailureCode Failure, IReadOnlyList<BiscuitCredentialRegistration>? Chain)> LoadChainAsync(
        BiscuitAuthenticatedWorkload workload, BiscuitCredentialRegistration leaf, CancellationToken ct)
    {
        var result = new List<BiscuitCredentialRegistration> { leaf };
        while (result[^1].ParentFingerprint is string parentId)
        {
            if (result.Count >= BiscuitProfile.MaximumBlocks) return (BiscuitFailureCode.InvalidCredential, null);
            var lookup = await _registry.FindAsync(workload.Actor, workload.Binding.Realm, workload.Binding.Context, parentId, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (lookup.Status != BiscuitRegistryStatus.Active) return (MapRegistry(lookup.Status), null);
            var parent = lookup.Registration; var child = result[^1];
            if (parent is null || parent.Binding != child.Binding ||
                parent.RootKeyId != child.RootKeyId || parent.GrantVersionIdentity != child.GrantVersionIdentity ||
                parent.AuthorityScopeId != child.AuthorityScopeId || parent.BlockCount + 1 != child.BlockCount ||
                !parent.RevocationIds.SequenceEqual(child.RevocationIds.Take(parent.RevocationIds.Count)) ||
                !BiscuitProfile.IsSubset(parent.EffectiveGrant, child.EffectiveGrant)) return (BiscuitFailureCode.InvalidCredential, null);
            result.Add(parent);
        }
        return (BiscuitFailureCode.None, result);
    }

    private static bool Applicable(AuthoritySnapshot current, BiscuitCredentialRegistration registration, DateTimeOffset now)
    {
        var selected = current.Layers.SingleOrDefault(l => l.Id == registration.LayerId)?.Grants.SingleOrDefault(g => g.Id == registration.RootGrant.Id);
        return current.Context == registration.Binding.Context && current.ValidUntil > now && selected is not null &&
            BiscuitProfile.SameGrant(selected, registration.RootGrant) &&
            registration.EffectiveGrant.NotBefore <= now && now < registration.EffectiveGrant.ExpiresAt;
    }
    private static BiscuitCredentialRegistration RegisterShape(BiscuitToken token, BiscuitWorkloadBinding binding,
        string keyId, string issuer, string layerId, string version, string scopeId, string restrictionId,
        AuthorityGrant rootGrant, AuthorityGrant effectiveGrant, string? parent, int checkCount)
    {
        var bytes = token.ToBytes(); var inspection = token.Inspect();
        return new(BiscuitProfile.Hash(bytes), bytes.Length, binding, keyId, issuer, layerId, version, scopeId, restrictionId,
            rootGrant, effectiveGrant, token.GetRevocationIds().Select(id => Convert.ToHexString(id.Value).ToLowerInvariant()).ToArray(),
            parent, inspection.BlockCount, inspection.BlockSources.Sum(BiscuitProfile.Utf8.GetByteCount), checkCount);
    }
    private static BiscuitFailureCode MapRegistry(BiscuitRegistryStatus status) => status switch
    {
        BiscuitRegistryStatus.Revoked => BiscuitFailureCode.AuthorityRevoked,
        BiscuitRegistryStatus.Unknown => BiscuitFailureCode.InvalidCredential,
        BiscuitRegistryStatus.Denied => BiscuitFailureCode.AuthorityDenied,
        BiscuitRegistryStatus.Conflict => BiscuitFailureCode.WorkflowAuthorityDenied,
        _ => BiscuitFailureCode.AuthorizationUnavailable,
    };
    private static string EvidenceJson(CedarEvaluationDetails cedar, BiscuitAuthorizationResult? biscuit, BiscuitFailureCode failure)
    {
        using var buffer = new MemoryStream();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject(); writer.WriteString("format", "hufu-biscuit-evidence-v1");
        writer.WriteString("failure", failure.ToString()); writer.WriteString("cedarEvaluator", cedar.Decision.EvaluatorIdentity);
        writer.WriteString("schema", cedar.SchemaDigest); writer.WriteString("entities", cedar.EntityDigest);
        writer.WriteStartArray("layers");
        foreach (var layer in cedar.Layers)
        {
            writer.WriteStartObject(); writer.WriteString("id", layer.LayerId); writer.WriteString("snapshot", layer.SnapshotDigest);
            writer.WriteString("schema", layer.SchemaDigest); writer.WriteString("policy", layer.PolicyDigest);
            writer.WriteString("entities", layer.EntityDigest); writer.WriteBoolean("policyValid", layer.PolicyValidation?.IsValid == true);
            writer.WriteBoolean("cleanAllow", layer.Authorization?.IsCleanAllow == true); writer.WriteEndObject();
        }
        writer.WriteEndArray(); writer.WriteStartArray("biscuitFindings");
        if (biscuit is not null) foreach (var error in biscuit.Errors)
        {
            writer.WriteStartObject(); writer.WriteString("code", error.Code);
            if (error.EvaluationFailureReason is { } reason) writer.WriteString("reason", reason.ToString());
            writer.WriteEndObject();
        }
        writer.WriteEndArray(); writer.WriteEndObject(); writer.Flush();
        if (buffer.Length > 16_384) throw new InvalidDataException("Evaluator evidence exceeds its bound.");
        return BiscuitProfile.Utf8.GetString(buffer.ToArray());
    }
}
