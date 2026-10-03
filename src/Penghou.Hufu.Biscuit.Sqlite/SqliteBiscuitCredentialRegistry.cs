using Microsoft.Data.Sqlite;
using Penghou.Hufu.Sqlite;

namespace Penghou.Hufu.Biscuit.Sqlite;

public sealed record SqliteBiscuitRegistryOptions
{
    public int MaxEntries { get; init; } = 100_000;
    public long MaxStoredBytes { get; init; } = 67_108_864;
}

/// <summary>
/// Authenticated exact-byte registrations and revocation tombstones in the host's
/// physical database. This store never persists bearer bytes or private keys.
/// </summary>
public sealed partial class SqliteBiscuitCredentialRegistry : IBiscuitCredentialRegistry
{
    private const int MaxBodyBytes = 262_144;
    private readonly ISqliteAuthorityDatabase _owner;
    private readonly string _path;
    private readonly IBiscuitRegistryAuthorizer _authorizer;
    private readonly SqliteBiscuitRegistryOptions _options;
    private TimeProvider Clock => _owner.TimeProvider;

    public SqliteBiscuitCredentialRegistry(ISqliteAuthorityDatabase owner, string physicalDatabasePath,
        IBiscuitRegistryAuthorizer authorizer, SqliteBiscuitRegistryOptions? options = null)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalDatabasePath);
        if (!Path.IsPathFullyQualified(physicalDatabasePath) || physicalDatabasePath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("One absolute physical disk database path is required.");
        _path = Path.GetFullPath(physicalDatabasePath);
        _options = options ?? new();
        if (_options.MaxEntries is < 1 or > 1_000_000 || _options.MaxStoredBytes is < 1 or > 268_435_456 ||
            owner.TimeProvider is null) throw new ArgumentOutOfRangeException(nameof(options));
    }

    public async ValueTask<BiscuitRegistrationLookup> FindAsync(AuthorityStoreActor actor, string realm,
        AuthenticatedAuthorityContext context, string fingerprint, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!ValidAccess(actor, realm, context) || !BiscuitProfile.IsHash(fingerprint)) return new(BiscuitRegistryStatus.Denied);
        try
        {
            var gate = await AuthorizeAsync(new(actor, BiscuitRegistryOperation.Lookup, realm, context, fingerprint), ct);
            if (gate != BiscuitRegistryStatus.Active) return new(gate);
            using var connection = await OpenAsync(ct);
            using var transaction = connection.BeginTransaction(deferred: false);
            await EnsureSchemaAsync(connection, transaction, ct);
            var found = await LoadRegistrationAsync(connection, transaction, realm, context.TenantId, fingerprint, ct);
            if (found is null) return new(BiscuitRegistryStatus.Unknown);
            if (found.Binding.Context != context) return new(BiscuitRegistryStatus.Denied);
            var status = await CheckActiveAsync(connection, transaction, found, Clock.GetUtcNow(), ct);
            transaction.Commit();
            return new(status, status == BiscuitRegistryStatus.Active ? found : null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return new(BiscuitRegistryStatus.Unavailable); }
    }

    public async ValueTask<BiscuitRegistryStatus> RegisterAsync(AuthorityStoreActor actor,
        BiscuitCredentialRegistration registration, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (registration is null || !ValidAccess(actor, registration.Binding.Realm, registration.Binding.Context))
            return BiscuitRegistryStatus.Denied;
        try
        {
            var gate = await AuthorizeAsync(new(actor, BiscuitRegistryOperation.Register, registration.Binding.Realm,
                registration.Binding.Context, registration.Fingerprint, registration.RootKeyId, registration), ct);
            if (gate != BiscuitRegistryStatus.Active) return gate;
            var body = BiscuitCodec.Encode(registration);
            if (body.Length > MaxBodyBytes) return BiscuitRegistryStatus.CapacityExceeded;
            using var connection = await OpenAsync(ct);
            using var transaction = connection.BeginTransaction(deferred: false);
            await EnsureSchemaAsync(connection, transaction, ct);
            var realm = registration.Binding.Realm; var tenant = actor.TenantId;
            var previous = await LoadRegistrationAsync(connection, transaction, realm, tenant, registration.Fingerprint, ct);
            if (previous is not null)
            {
                if (!BiscuitCodec.Encode(previous).AsSpan().SequenceEqual(body)) return BiscuitRegistryStatus.Conflict;
                var active = await CheckActiveAsync(connection, transaction, previous, Clock.GetUtcNow(), ct);
                return active == BiscuitRegistryStatus.Active ? BiscuitRegistryStatus.Replayed : active;
            }
            var meaning = await ScalarAsync(connection, transaction,
                "SELECT meaning FROM main.hufu_biscuit_versions WHERE realm=$realm AND tenant=$tenant AND layer=$layer AND grant_id=$grant AND version=$version", ct,
                ("$realm", realm), ("$tenant", tenant), ("$layer", registration.LayerId),
                ("$grant", registration.RootGrant.Id), ("$version", registration.GrantVersion));
            if (meaning is not null && !Equals(meaning, registration.GrantVersionIdentity)) return BiscuitRegistryStatus.Conflict;
            if (registration.ParentFingerprint is string parentId)
            {
                var parent = await LoadRegistrationAsync(connection, transaction, realm, tenant, parentId, ct);
                if (parent is null || !ValidChild(parent, registration)) return BiscuitRegistryStatus.Conflict;
                var active = await CheckActiveAsync(connection, transaction, parent, Clock.GetUtcNow(), ct);
                if (active != BiscuitRegistryStatus.Active) return active;
            }
            else if (registration.IssuerId != actor.ActorId || registration.CheckCount != 0 ||
                registration.AuthorityScopeId != registration.RestrictionId ||
                !BiscuitProfile.SameGrant(registration.RootGrant, registration.EffectiveGrant))
                return BiscuitRegistryStatus.Denied;
            var revocation = await CheckRevocationAsync(connection, transaction, registration, ct);
            if (revocation != BiscuitRegistryStatus.Active) return revocation;
            var now = Clock.GetUtcNow();
            if (registration.EffectiveGrant.NotBefore > now || registration.EffectiveGrant.ExpiresAt <= now)
                return BiscuitRegistryStatus.Conflict;
            if (!await HasCapacityAsync(connection, transaction, body.Length + TextBytes(realm,tenant,registration.Fingerprint,actor.ActorId,actor.SessionId) + 64 + (meaning is null ? TextBytes(realm,tenant,registration.LayerId,registration.RootGrant.Id,registration.GrantVersion) + 64 : 0), meaning is null ? 2 : 1, ct))
                return BiscuitRegistryStatus.CapacityExceeded;
            if (meaning is null)
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO main.hufu_biscuit_versions VALUES($realm,$tenant,$layer,$grant,$version,$meaning)", ct,
                    ("$realm", realm), ("$tenant", tenant), ("$layer", registration.LayerId),
                    ("$grant", registration.RootGrant.Id), ("$version", registration.GrantVersion), ("$meaning", registration.GrantVersionIdentity));
            await ExecuteAsync(connection, transaction,
                "INSERT INTO main.hufu_biscuit_credentials VALUES($realm,$tenant,$fingerprint,$body,$hash,$actor,$session)", ct,
                ("$realm", realm), ("$tenant", tenant), ("$fingerprint", registration.Fingerprint),
                ("$body", body), ("$hash", BiscuitProfile.Hash(body)), ("$actor", actor.ActorId), ("$session", actor.SessionId));
            ct.ThrowIfCancellationRequested();
            if (registration.EffectiveGrant.ExpiresAt <= Clock.GetUtcNow()) return BiscuitRegistryStatus.Conflict;
            transaction.Commit();
            return BiscuitRegistryStatus.Registered;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return BiscuitRegistryStatus.Unavailable; }
    }

    public async ValueTask<BiscuitRegistryStatus> CheckAsync(AuthorityStoreActor actor,
        BiscuitCredentialRegistration registration, DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (registration is null || !ValidAccess(actor, registration.Binding.Realm, registration.Binding.Context) || now.Offset != TimeSpan.Zero)
            return BiscuitRegistryStatus.Denied;
        try
        {
            var gate = await AuthorizeAsync(new(actor, BiscuitRegistryOperation.Check, registration.Binding.Realm,
                registration.Binding.Context, registration.Fingerprint, registration.RootKeyId, registration), ct);
            if (gate != BiscuitRegistryStatus.Active) return gate;
            using var connection = await OpenAsync(ct);
            using var transaction = connection.BeginTransaction(deferred: false);
            await EnsureSchemaAsync(connection, transaction, ct);
            var stored = await LoadRegistrationAsync(connection, transaction, registration.Binding.Realm, actor.TenantId, registration.Fingerprint, ct);
            if (stored is null) return BiscuitRegistryStatus.Unknown;
            if (!BiscuitCodec.Encode(stored).AsSpan().SequenceEqual(BiscuitCodec.Encode(registration))) return BiscuitRegistryStatus.Conflict;
            var actualNow = Clock.GetUtcNow();
            var result = await CheckActiveAsync(connection, transaction, stored, now > actualNow ? now : actualNow, ct);
            transaction.Commit();
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return BiscuitRegistryStatus.Unavailable; }
    }

    private static bool ValidAccess(AuthorityStoreActor actor, string realm, AuthenticatedAuthorityContext? context) =>
        AuthorityStoreValidation.ValidActor(actor) && AuthorityValidation.ValidToken(realm) &&
        (context is null || AuthorityValidation.ValidContext(context) && context.TenantId == actor.TenantId);

    private async ValueTask<BiscuitRegistryStatus> AuthorizeAsync(BiscuitRegistryAccess access, CancellationToken ct)
    {
        var auth = await _authorizer.AuthorizeAsync(access, ct);
        ct.ThrowIfCancellationRequested();
        return auth?.Status == AuthorityStatus.Permit && auth.Actor == access.Actor ? BiscuitRegistryStatus.Active :
            auth?.Status == AuthorityStatus.Deny ? BiscuitRegistryStatus.Denied : BiscuitRegistryStatus.Unavailable;
    }

    private static bool ValidChild(BiscuitCredentialRegistration parent, BiscuitCredentialRegistration child) =>
        parent.Binding == child.Binding && parent.RootKeyId == child.RootKeyId && parent.IssuerId == child.IssuerId &&
        parent.GrantVersionIdentity == child.GrantVersionIdentity && parent.AuthorityScopeId == child.AuthorityScopeId &&
        child.RestrictionId != parent.RestrictionId && child.BlockCount == parent.BlockCount + 1 &&
        child.CheckCount == parent.CheckCount + 2 && child.SourceBytes > parent.SourceBytes &&
        parent.RevocationIds.SequenceEqual(child.RevocationIds.Take(parent.BlockCount)) &&
        BiscuitProfile.IsSubset(parent.EffectiveGrant, child.EffectiveGrant);
}
