using Microsoft.Data.Sqlite;
using Penghou.Hufu.Sqlite;
using System.Text.Json;

namespace Penghou.Hufu.Biscuit.Sqlite;

public sealed partial class SqliteBiscuitCredentialRegistry
{
    public async ValueTask<BiscuitRegistryStatus> RevokeAsync(AuthorityStoreActor actor, string realm,
        AuthenticatedAuthorityContext context, string fingerprint, string reasonCode, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!ValidAccess(actor, realm, context) || !BiscuitProfile.IsHash(fingerprint) || !AuthorityValidation.ValidToken(reasonCode))
            return BiscuitRegistryStatus.Denied;
        try
        {
            var gate = await AuthorizeAsync(new(actor, BiscuitRegistryOperation.Revoke, realm, context, fingerprint, ReasonCode: reasonCode), ct);
            if (gate != BiscuitRegistryStatus.Active) return gate;
            using var connection = await OpenAsync(ct);
            using var transaction = connection.BeginTransaction(deferred: false);
            await EnsureSchemaAsync(connection, transaction, ct);
            var credential = await LoadRegistrationAsync(connection, transaction, realm, actor.TenantId, fingerprint, ct);
            if (credential is null) return BiscuitRegistryStatus.Unknown;
            if (credential.Binding.Context != context) return BiscuitRegistryStatus.Denied;
            var id = credential.RevocationIds[^1];
            if (await ScalarAsync(connection, transaction,
                "SELECT 1 FROM main.hufu_biscuit_revocations WHERE realm=$realm AND tenant=$tenant AND id=$id", ct,
                ("$realm", realm), ("$tenant", actor.TenantId), ("$id", id)) is not null) return BiscuitRegistryStatus.Replayed;
            if (!await HasCapacityAsync(connection, transaction, TextBytes(realm, actor.TenantId, id, actor.ActorId, actor.SessionId, reasonCode)+8, 1, ct))
                return BiscuitRegistryStatus.CapacityExceeded;
            await ExecuteAsync(connection, transaction,
                "INSERT INTO main.hufu_biscuit_revocations VALUES($realm,$tenant,$id,$actor,$session,$reason,$at)", ct,
                ("$realm", realm), ("$tenant", actor.TenantId), ("$id", id), ("$actor", actor.ActorId),
                ("$session", actor.SessionId), ("$reason", reasonCode), ("$at", Clock.GetUtcNow().UtcTicks));
            ct.ThrowIfCancellationRequested();
            transaction.Commit(); // Same writer ordering as operation start.
            return BiscuitRegistryStatus.Recorded;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return BiscuitRegistryStatus.Unavailable; }
    }

    /// <summary>Realm-wide retirement requires explicit host administrative authentication.</summary>
    public async ValueTask<BiscuitRegistryStatus> RetireKeyAsync(AuthorityStoreActor actor, string realm,
        string keyId, string reasonCode, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!ValidAccess(actor, realm, null) || !AuthorityValidation.ValidToken(keyId) || !AuthorityValidation.ValidToken(reasonCode))
            return BiscuitRegistryStatus.Denied;
        try
        {
            var gate = await AuthorizeAsync(new(actor, BiscuitRegistryOperation.RetireKey, realm, KeyId: keyId, ReasonCode: reasonCode), ct);
            if (gate != BiscuitRegistryStatus.Active) return gate;
            using var connection = await OpenAsync(ct);
            using var transaction = connection.BeginTransaction(deferred: false);
            await EnsureSchemaAsync(connection, transaction, ct);
            if (await ScalarAsync(connection, transaction,
                "SELECT 1 FROM main.hufu_biscuit_retired_keys WHERE realm=$realm AND id=$id", ct,
                ("$realm", realm), ("$id", keyId)) is not null) return BiscuitRegistryStatus.Replayed;
            if (!await HasCapacityAsync(connection, transaction, TextBytes(realm,keyId,actor.TenantId,actor.ActorId,actor.SessionId,reasonCode)+8, 1, ct))
                return BiscuitRegistryStatus.CapacityExceeded;
            await ExecuteAsync(connection, transaction,
                "INSERT INTO main.hufu_biscuit_retired_keys VALUES($realm,$id,$tenant,$actor,$session,$reason,$at)", ct,
                ("$realm", realm), ("$id", keyId), ("$tenant", actor.TenantId), ("$actor", actor.ActorId),
                ("$session", actor.SessionId), ("$reason", reasonCode), ("$at", Clock.GetUtcNow().UtcTicks));
            ct.ThrowIfCancellationRequested();
            transaction.Commit();
            return BiscuitRegistryStatus.Recorded;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return BiscuitRegistryStatus.Unavailable; }
    }

    public async ValueTask<BiscuitRegistryStatus> RecordVerificationAsync(BiscuitDecisionEvidence evidence, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            if (!ValidEvidence(evidence)) return BiscuitRegistryStatus.Denied;
            var gate = await AuthorizeAsync(new(evidence.Actor, BiscuitRegistryOperation.RecordVerification,
                evidence.Realm, evidence.Request.Context, evidence.Fingerprint, evidence.RootKeyId, Evidence: evidence), ct);
            if (gate != BiscuitRegistryStatus.Active) return gate;
            var body = BiscuitCodec.Encode(evidence);
            if (body.Length > MaxBodyBytes) return BiscuitRegistryStatus.CapacityExceeded;
            using var connection = await OpenAsync(ct);
            using var transaction = connection.BeginTransaction(deferred: false);
            await EnsureSchemaAsync(connection, transaction, ct);
            var previous = await LoadEvidenceAsync(connection, transaction, evidence.Actor.TenantId, evidence.Id, ct);
            if (previous is not null)
                return BiscuitCodec.Encode(previous).AsSpan().SequenceEqual(body) ? BiscuitRegistryStatus.Replayed : BiscuitRegistryStatus.Conflict;
            var credential = await LoadRegistrationAsync(connection, transaction, evidence.Realm, evidence.Actor.TenantId, evidence.Fingerprint, ct);
            if (credential is null || credential.Binding.Context != evidence.Request.Context || credential.RootKeyId != evidence.RootKeyId)
                return BiscuitRegistryStatus.Unknown;
            var now = Clock.GetUtcNow();
            if (evidence.EvaluatedAt > now) return BiscuitRegistryStatus.Conflict;
            if (evidence.Decision.Status == AuthorityStatus.Permit)
            {
                var active = await CheckActiveAsync(connection, transaction, credential, now, ct);
                if (active != BiscuitRegistryStatus.Active) return active;
                if (evidence.ValidUntil > credential.EffectiveGrant.ExpiresAt || evidence.ValidUntil <= now ||
                    !BiscuitProfile.Covers(credential.EffectiveGrant, evidence.Request, now)) return BiscuitRegistryStatus.Conflict;
            }
            if (!await HasCapacityAsync(connection, transaction, body.Length+TextBytes(evidence.Actor.TenantId,evidence.Id)+64, 1, ct))
                return BiscuitRegistryStatus.CapacityExceeded;
            await ExecuteAsync(connection, transaction,
                "INSERT INTO main.hufu_biscuit_verifications VALUES($tenant,$id,$body,$hash)", ct,
                ("$tenant", evidence.Actor.TenantId), ("$id", evidence.Id), ("$body", body), ("$hash", BiscuitProfile.Hash(body)));
            ct.ThrowIfCancellationRequested();
            if (evidence.Decision.Status == AuthorityStatus.Permit && evidence.ValidUntil <= Clock.GetUtcNow()) return BiscuitRegistryStatus.Conflict;
            transaction.Commit();
            return BiscuitRegistryStatus.Recorded;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return BiscuitRegistryStatus.Unavailable; }
    }

    internal async ValueTask<SqliteRuntimeStartResult> CheckStartAsync(SqliteConnection connection, SqliteTransaction transaction,
        AuthorityOperationStartCommand command, string engineIdentity, string mappingIdentity, string evaluatorIdentity, CancellationToken ct)
    {
        try
        {
            await ValidateDatabaseAsync(connection, ct);
            // No schema creation, authentication, external policy or native evaluation inside a protected start.
            if (!Equals(await ScalarAsync(connection, transaction, "SELECT codec FROM main.hufu_biscuit_meta WHERE id=1", ct), "hufu-biscuit-json-v1"))
                return new(AuthorityStatus.Unavailable, DateTimeOffset.MinValue);
            var evidence = await LoadEvidenceAsync(connection, transaction, command.Actor.TenantId, command.DecisionCommandId, ct);
            var now = Clock.GetUtcNow();
            if (evidence is null || evidence.Actor.TenantId != command.Actor.TenantId || evidence.Actor.ActorId != command.Actor.ActorId ||
                evidence.Request != command.Request || evidence.Decision.Status != AuthorityStatus.Permit ||
                evidence.Resource is null || evidence.Resource.StartBindingIdentity != command.BindingIdentity ||
                evidence.EngineIdentity != engineIdentity || evidence.MappingIdentity != mappingIdentity ||
                evidence.Decision.EvaluatorIdentity != evaluatorIdentity || evidence.EvaluatedAt > now || evidence.ValidUntil <= now)
                return new(AuthorityStatus.Deny, DateTimeOffset.MinValue);
            // The core gate already validated this row and its hash/receipt in this transaction.
            // Require its exact recorded Biscuit evidence, not merely another Permit with the same ID.
            var coreEvidence = await ScalarAsync(connection, transaction,
                "SELECT CASE WHEN typeof(body)='blob' AND length(body)<=2097152 THEN json_extract(CAST(body AS TEXT),'$.Record.EvidenceJson') END FROM main.hufu_decisions WHERE tenant_id=$tenant AND command_id=$id", ct,
                ("$tenant", command.Actor.TenantId), ("$id", command.DecisionCommandId));
            if (!Equals(coreEvidence, BiscuitProfile.Utf8.GetString(BiscuitCodec.Encode(evidence))))
                return new(AuthorityStatus.Deny, DateTimeOffset.MinValue);
            var credential = await LoadRegistrationAsync(connection, transaction, evidence.Realm, command.Actor.TenantId, evidence.Fingerprint, ct);
            if (credential is null || credential.Binding.Context != command.Request.Context || credential.RootKeyId != evidence.RootKeyId)
                return new(AuthorityStatus.Deny, DateTimeOffset.MinValue);
            var active = await CheckActiveAsync(connection, transaction, credential, now, ct);
            if (active != BiscuitRegistryStatus.Active || !BiscuitProfile.Covers(credential.EffectiveGrant, command.Request, now))
                return new(AuthorityStatus.Deny, DateTimeOffset.MinValue);
            return new(AuthorityStatus.Permit, evidence.ValidUntil < credential.EffectiveGrant.ExpiresAt ?
                evidence.ValidUntil : credential.EffectiveGrant.ExpiresAt);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return new(AuthorityStatus.Unavailable, DateTimeOffset.MinValue); }
    }

    private static bool ValidEvidence(BiscuitDecisionEvidence? evidence)
    {
        if (evidence is null || !ValidAccess(evidence.Actor, evidence.Realm, evidence.Request?.Context) ||
            !AuthorityValidation.IsValidRequest(evidence.Request) || !AuthorityValidation.ValidToken(evidence.Id) ||
            !BiscuitProfile.IsHash(evidence.Fingerprint) || !AuthorityValidation.ValidToken(evidence.RootKeyId) ||
            !AuthorityValidation.ValidToken(evidence.EngineIdentity) || !AuthorityValidation.ValidToken(evidence.MappingIdentity) ||
            evidence.Decision is not { } decision || !Enum.IsDefined(decision.Status) ||
            !AuthorityValidation.ValidToken(decision.ReasonCode) || !AuthorityValidation.ValidToken(decision.SnapshotVersion) ||
            !AuthorityValidation.ValidToken(decision.EvaluatorIdentity) || !BiscuitProfile.IsHash(decision.SnapshotIdentity) ||
            evidence.EvaluatedAt.Offset != TimeSpan.Zero || evidence.ValidUntil.Offset != TimeSpan.Zero ||
            evidence.EvidenceJson is null || BiscuitProfile.Utf8.GetByteCount(evidence.EvidenceJson) > 16_384) return false;
        if (evidence.Resource is { } resource &&
            (resource.Request != evidence.Request || !AuthorityValidation.ValidToken(resource.ProviderIdentity) ||
            !AuthorityValidation.ValidToken(resource.ObjectIdentity) || !AuthorityValidation.ValidToken(resource.EffectIdentity) ||
            resource.StartBindingIdentity is not null && !BiscuitProfile.IsHash(resource.StartBindingIdentity))) return false;
        if (decision.Status == AuthorityStatus.Permit && (evidence.Resource is null || evidence.EvaluatedAt >= evidence.ValidUntil ||
            decision.ReasonCode != BiscuitFailureCode.None.ToString())) return false;
        try
        {
            using var json = JsonDocument.Parse(evidence.EvidenceJson, new() { MaxDepth = 32 });
            return json.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch { return false; }
    }
}
