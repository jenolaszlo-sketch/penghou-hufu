using Microsoft.Data.Sqlite;

namespace Penghou.Hufu.Sqlite;

/// <summary>
/// Atomic derived-authority persistence for <see cref="SqliteAuthorityStore"/>.
/// Derivation lineage, the child snapshot publication, and the idempotency
/// record commit in one transaction: either all become visible together or
/// none do. There is no state in which a usable child exists without the
/// lineage required to constrain it.
/// </summary>
public sealed partial class SqliteAuthorityStore : IAuthorityDerivationStore
{
    public async ValueTask<AuthorityGrantDerivationResult> DeriveAsync(
        AuthorityDerivationCommand command,
        CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(command);
        var key = AuthorityDerivation.DerivationIdentity(
            command.ParentGrantId, command.DelegationId, command.Generation, command.Requested);
        try
        {
            // Permission to attempt issuance is established by the trusted
            // authorizer before any state is touched; the store operation then
            // protects authority integrity (parent liveness, containment) at
            // commit. The duplication is intentional.
            var access = new AuthorityStoreAccessRequest(
                command.Actor, AuthorityStoreOperation.Derive, AuthoritySubject.From(command.ChildContext),
                command.ChildContext, DerivationCommand: command);
            var authorization = await AuthorizeAsync(access, ct).ConfigureAwait(false);
            if (authorization.Status != AuthorityStatus.Permit)
            {
                return authorization.Status == AuthorityStatus.Deny
                    ? new AuthorityGrantDerivationResult(AuthorityStatus.Deny, null, command.ParentGrantId, key, "authority.derivation-not-authorized")
                    : new AuthorityGrantDerivationResult(AuthorityStatus.Unavailable, null, command.ParentGrantId, key, "authority.store-unavailable");
            }

            using var connection = await OpenAsync(ct).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            var replayed = await LoadDerivationAsync(connection, transaction, key, ct).ConfigureAwait(false);
            if (replayed is not null)
                return Issued(replayed);
            var outcome = await DeriveInTransactionAsync(connection, transaction, command, key, ct).ConfigureAwait(false);
            if (outcome.Terminal is not null)
                return outcome.Terminal;
            transaction.Commit();
            return Issued(outcome.Built ?? throw new InvalidDataException());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return new AuthorityGrantDerivationResult(AuthorityStatus.Unavailable, null, command.ParentGrantId, key, "authority.store-unavailable"); }
    }

    public async ValueTask<AuthorityGrantLineage?> GetLineageAsync(
        string childGrantId,
        CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        ct.ThrowIfCancellationRequested();
        if (!AuthorityValidation.ValidToken(childGrantId))
            throw new ArgumentException("A bounded child grant identity is required.", nameof(childGrantId));
        try
        {
            using var connection = await OpenAsync(ct).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: true);
            using var command = Command(connection, transaction,
                "SELECT body,body_hash FROM hufu_derivations WHERE child_grant_id=$grant", ("$grant", childGrantId));
            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return null;
            if (reader.IsDBNull(0)) throw new InvalidDataException();
            var lineage = StoreCodec.Decode<DerivationData>((byte[])reader.GetValue(0), reader.GetString(1)).ToLineage();
            if (lineage.ChildGrantId != childGrantId) throw new InvalidDataException();
            return lineage;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    private async ValueTask<(AuthorityGrantDerivationResult? Terminal, AuthorityGrantLineage? Built)> DeriveInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AuthorityDerivationCommand command,
        string key,
        CancellationToken ct)
    {
        var parentKey = StoreCodec.SubjectKey(AuthoritySubject.From(command.ParentContext));
        var parentHead = await HeadAsync(connection, transaction, parentKey, ct).ConfigureAwait(false);
        if (parentHead is null || parentHead.Kind != AuthorityChangeKind.Published)
            return (new AuthorityGrantDerivationResult(AuthorityStatus.Unavailable, null,
                command.ParentGrantId, key, "authority.parent-unavailable"), null);
        var parentRecord = await LoadChangeAsync(connection, transaction, parentKey, parentHead.Sequence, ct)
            .ConfigureAwait(false);
        if (parentRecord.Context != command.ParentContext || parentRecord.Snapshot is not { } parentSnapshot)
            return (new AuthorityGrantDerivationResult(AuthorityStatus.Unavailable, null,
                command.ParentGrantId, key, "authority.parent-unavailable"), null);
        var parentGrant = parentSnapshot.Layers
            .SelectMany(layer => layer.Grants)
            .FirstOrDefault(grant => grant.Id == command.ParentGrantId);
        var now = _clock.GetUtcNow();
        if (parentGrant is null || !(parentGrant.NotBefore <= now && now < parentGrant.ExpiresAt) ||
            parentSnapshot.ValidUntil <= now)
            return (new AuthorityGrantDerivationResult(AuthorityStatus.Unavailable, null,
                command.ParentGrantId, key, "authority.parent-unavailable"), null);
        if (!AuthorityDerivation.IsContainedBy(command.Requested, parentGrant))
            return (new AuthorityGrantDerivationResult(AuthorityStatus.Deny, null,
                command.ParentGrantId, key, "authority.derivation-not-contained"), null);

        var childGrantId = "derived-" + key.Substring(0, 32);
        var childGrant = new AuthorityGrant(
            childGrantId,
            command.Requested.Actions,
            command.Requested.Scope,
            command.Requested.Exclusions,
            command.Requested.NotBefore,
            command.Requested.ExpiresAt,
            command.ParentGrantId);
        var childSnapshot = new AuthoritySnapshot(
            command.ChildContext,
            "derived-" + key.Substring(0, 8),
            [new AuthorityLayer("derived", [childGrant])],
            [],
            command.Requested.ExpiresAt);
        var childKey = StoreCodec.SubjectKey(AuthoritySubject.From(command.ChildContext));
        var childHead = await HeadAsync(connection, transaction, childKey, ct).ConfigureAwait(false);
        if (childHead?.Kind == AuthorityChangeKind.Revoked)
            return (new AuthorityGrantDerivationResult(AuthorityStatus.Unavailable, null,
                command.ParentGrantId, key, "authority.child-slot-revoked"), null);
        if (childHead is not null && childHead.Context != command.ChildContext)
        {
            if (await ExistsAsync(connection, transaction,
                "SELECT 1 FROM hufu_events WHERE subject_key=$key AND context_identity=$context", ct,
                ("$key", childKey), ("$context", ContextIdentity(command.ChildContext))).ConfigureAwait(false))
                return (new AuthorityGrantDerivationResult(AuthorityStatus.Unavailable, null,
                    command.ParentGrantId, key, "authority.child-slot-conflict"), null);
        }
        var childSequence = (childHead?.Sequence ?? 0) + 1;

        var lineage = new AuthorityGrantLineage(
            key, childGrantId, command.ChildContext, command.ParentGrantId, command.ParentContext,
            command.DelegationId, command.Generation, command.Requested, command.Actor, now);
        var lineageBody = StoreCodec.Encode(DerivationData.From(lineage));
        try
        {
            await ExecuteAsync(connection, transaction,
                "INSERT INTO hufu_derivations(derivation_identity,child_grant_id,child_tenant,child_subject,child_run,body,body_hash,issued_at) " +
                "VALUES($key,$grant,$tenant,$subject,$run,$body,$hash,$issued)", ct,
                ("$key", key), ("$grant", childGrantId),
                ("$tenant", command.ChildContext.TenantId), ("$subject", command.ChildContext.SubjectId), ("$run", command.ChildContext.RunId),
                ("$body", lineageBody), ("$hash", StoreCodec.Hash(lineageBody)), ("$issued", now)).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            var winner = await LoadDerivationAsync(connection, transaction, key, ct).ConfigureAwait(false);
            if (winner is null) throw new InvalidDataException();
            return (Issued(winner), null);
        }

        var record = new AuthorityChangeRecord(childSequence, AuthorityChangeKind.Published, key,
            command.Actor, command.ChildContext, childSnapshot, "authority.derived", now);
        var body = StoreCodec.Encode(StoreCodec.ChangeData.From(record));
        if (!await ReserveAsync(connection, transaction, false, body.Length, ct).ConfigureAwait(false))
            throw new InvalidDataException();
        await ExecuteAsync(connection, transaction,
            "INSERT INTO hufu_events(subject_key,sequence,context_identity,body,body_hash) VALUES($key,$seq,$context,$body,$hash)", ct,
            ("$key", childKey), ("$seq", record.Sequence), ("$context", ContextIdentity(record.Context)),
            ("$body", body), ("$hash", StoreCodec.Hash(body))).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction,
            "INSERT INTO hufu_versions(subject_key,version,snapshot_identity,sequence) VALUES($key,$version,$identity,$seq)", ct,
            ("$key", childKey), ("$version", childSnapshot.Version), ("$identity", childSnapshot.Identity), ("$seq", record.Sequence)).ConfigureAwait(false);
        // The change reader cross-checks every event against its command receipt,
        // so the derivation registers exactly like a direct publication would.
        var derivedAccess = new AuthorityStoreAccessRequest(command.Actor, AuthorityStoreOperation.Publish,
            AuthoritySubject.From(command.ChildContext), command.ChildContext, key, record.Sequence - 1,
            childSnapshot, "authority.derived");
        await SaveCommandAsync(connection, transaction, command.Actor.TenantId, key, 1,
            Intent(derivedAccess, 1), childKey, record.Sequence, ct).ConfigureAwait(false);
        if (childHead is null)
        {
            await ExecuteAsync(connection, transaction,
                "INSERT INTO hufu_head(subject_key,sequence) VALUES($key,$seq)", ct,
                ("$key", childKey), ("$seq", record.Sequence)).ConfigureAwait(false);
        }
        else if (await ExecuteAsync(connection, transaction,
            "UPDATE hufu_head SET sequence=$seq WHERE subject_key=$key AND sequence=$expected", ct,
            ("$key", childKey), ("$seq", record.Sequence), ("$expected", childHead.Sequence)).ConfigureAwait(false) != 1)
            throw new InvalidDataException();
        return (null, lineage);
    }

    private static AuthorityGrantDerivationResult Issued(AuthorityGrantLineage lineage) =>
        new(AuthorityStatus.Permit, RebuildGrant(lineage), lineage.ParentGrantId, lineage.DerivationIdentity, null);

    private static AuthorityGrant RebuildGrant(AuthorityGrantLineage lineage) =>
        new(lineage.ChildGrantId, lineage.Requested.Actions, lineage.Requested.Scope,
            lineage.Requested.Exclusions, lineage.Requested.NotBefore, lineage.Requested.ExpiresAt,
            lineage.ParentGrantId);

    private static async ValueTask<AuthorityGrantLineage?> LoadDerivationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        CancellationToken ct)
    {
        using var command = Command(connection, transaction,
            "SELECT body,body_hash,child_grant_id FROM hufu_derivations WHERE derivation_identity=$key",
            ("$key", key));
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;
        if (reader.IsDBNull(0)) throw new InvalidDataException();
        var lineage = StoreCodec.Decode<DerivationData>((byte[])reader.GetValue(0), reader.GetString(1)).ToLineage();
        if (lineage.DerivationIdentity != key || lineage.ChildGrantId != reader.GetString(2)) throw new InvalidDataException();
        return lineage;
    }

    internal sealed record DerivationData(
        string DerivationIdentity,
        string ChildGrantId,
        AuthenticatedAuthorityContext ChildContext,
        string ParentGrantId,
        AuthenticatedAuthorityContext ParentContext,
        string DelegationId,
        string Generation,
        RequestedAuthority Requested,
        AuthorityStoreActor Actor,
        DateTimeOffset IssuedAt)
    {
        internal static DerivationData From(AuthorityGrantLineage lineage) => new(
            lineage.DerivationIdentity, lineage.ChildGrantId, lineage.ChildContext, lineage.ParentGrantId,
            lineage.ParentContext, lineage.DelegationId, lineage.Generation, lineage.Requested,
            lineage.Actor, lineage.IssuedAt);

        internal AuthorityGrantLineage ToLineage()
        {
            var lineage = new AuthorityGrantLineage(
                DerivationIdentity, ChildGrantId, ChildContext, ParentGrantId, ParentContext,
                DelegationId, Generation, Requested, Actor, IssuedAt);
            if (AuthorityDerivation.DerivationIdentity(lineage.ParentGrantId, lineage.DelegationId,
                    lineage.Generation, lineage.Requested) != lineage.DerivationIdentity)
                throw new InvalidDataException();
            return lineage;
        }
    }
}
