using Microsoft.Data.Sqlite;

namespace Penghou.Hufu.Sqlite;

public sealed record SqliteAuthorityStoreOptions
{
    public int MaxAuthorityEvents { get; init; } = 10_000;
    public int MaxDecisionEntries { get; init; } = 100_000;
    public long MaxStoredBytes { get; init; } = 67_108_864;
}

/// <summary>Optional durable current authority and attributable evidence. Not a protected-I/O start gate.</summary>
public sealed partial class SqliteAuthorityStore : IAuthorityStore
{
    private const long ApplicationId = 0x48554655;
    private const int SchemaVersion = 2;
    private readonly string? _connectionString;
    private readonly ISqliteAuthorityDatabase? _databaseOwner;
    private readonly IAuthorityStoreAuthorizer _authorizer;
    private readonly TimeProvider _clock;
    private readonly SqliteAuthorityStoreOptions _options;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026", Justification = "Preserve existing preview signatures: disk-path and trusted database-owner overloads have distinct first-parameter types.")]
    public SqliteAuthorityStore(string databasePath, IAuthorityStoreAuthorizer authorizer,
        TimeProvider? timeProvider = null, SqliteAuthorityStoreOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (databasePath == ":memory:" || databasePath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A dedicated disk database path is required.", nameof(databasePath));
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        _clock = timeProvider ?? TimeProvider.System;
        _options = options ?? new();
        if (_options.MaxAuthorityEvents is < 1 or > 100_000 || _options.MaxDecisionEntries is < 1 or > 1_000_000 ||
            _options.MaxStoredBytes is < 1 or > 268_435_456) throw new ArgumentOutOfRangeException(nameof(options));
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 1
        }.ToString();
    }

    /// <summary>
    /// Explicit host composition for co-located authority/runtime transactions.
    /// The owner authenticates and initializes its other schemas; Hufu owns only hufu_* tables.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026", Justification = "Preserve existing preview signatures: disk-path and trusted database-owner overloads have distinct first-parameter types.")]
    public SqliteAuthorityStore(ISqliteAuthorityDatabase databaseOwner, IAuthorityStoreAuthorizer authorizer,
        SqliteAuthorityStoreOptions? options = null)
    {
        _databaseOwner = databaseOwner ?? throw new ArgumentNullException(nameof(databaseOwner));
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        _clock = databaseOwner.TimeProvider ?? throw new ArgumentException("An authoritative clock is required.", nameof(databaseOwner));
        _options = options ?? new();
        if (_options.MaxAuthorityEvents is < 1 or > 100_000 || _options.MaxDecisionEntries is < 1 or > 1_000_000 ||
            _options.MaxStoredBytes is < 1 or > 268_435_456) throw new ArgumentOutOfRangeException(nameof(options));
    }

    public ValueTask<AuthorityMutationResult> PublishAsync(AuthorityPublishCommand command, CancellationToken cancellationToken = default)
    {
        if (command is null || !AuthorityStoreValidation.ValidActor(command.Actor) || command.Snapshot is null ||
            !AuthorityValidation.ValidToken(command.CommandId) || command.ExpectedSequence < 0)
            return InvalidMutation(cancellationToken);
        return ChangeAsync(new(command.Actor, AuthorityStoreOperation.Publish, AuthoritySubject.From(command.Snapshot.Context),
            command.Snapshot.Context, command.CommandId, command.ExpectedSequence, command.Snapshot, "authority.published"), cancellationToken);
    }

    public ValueTask<AuthorityMutationResult> RevokeAsync(AuthorityRevokeCommand command, CancellationToken cancellationToken = default)
    {
        if (command is null || !AuthorityStoreValidation.ValidActor(command.Actor) || !AuthorityValidation.ValidContext(command.Context) ||
            !AuthorityValidation.ValidToken(command.CommandId) || !AuthorityValidation.ValidToken(command.ReasonCode) || command.ExpectedSequence < 0)
            return InvalidMutation(cancellationToken);
        return ChangeAsync(new(command.Actor, AuthorityStoreOperation.Revoke, AuthoritySubject.From(command.Context),
            command.Context, command.CommandId, command.ExpectedSequence, ReasonCode: command.ReasonCode), cancellationToken);
    }

    private async ValueTask<AuthorityMutationResult> ChangeAsync(AuthorityStoreAccessRequest access, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var auth = await AuthorizeAsync(access, ct).ConfigureAwait(false);
            if (auth.Status != AuthorityStatus.Permit) return new(MutationAuthStatus(auth.Status));
            using var connection = await OpenAsync(ct).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            var key = StoreCodec.SubjectKey(access.Subject);
            var kind = access.Operation == AuthorityStoreOperation.Publish ? AuthorityChangeKind.Published : AuthorityChangeKind.Revoked;
            var intent = Intent(access, kind == AuthorityChangeKind.Published ? 1 : 2);
            var prior = await FindCommandAsync(connection, transaction, access.Subject.TenantId, access.CommandId!, ct).ConfigureAwait(false);
            if (prior is not null)
            {
                if (prior.Intent != intent || prior.Kind != (kind == AuthorityChangeKind.Published ? 1 : 2) || prior.SubjectKey != key)
                    return new(AuthorityMutationStatus.Conflict);
                var receipt = await LoadChangeAsync(connection, transaction, key, prior.Sequence, ct).ConfigureAwait(false);
                if (receipt.CommandId != access.CommandId || receipt.Actor.ActorId != auth.Actor!.ActorId) throw new InvalidDataException();
                return new(AuthorityMutationStatus.Replayed, receipt);
            }
            var head = await HeadAsync(connection, transaction, key, ct).ConfigureAwait(false);
            if ((head?.Sequence ?? 0) != access.ExpectedSequence || head?.Kind == AuthorityChangeKind.Revoked || head?.Sequence == long.MaxValue)
                return new(AuthorityMutationStatus.Conflict);
            var now = _clock.GetUtcNow();
            if (head is not null && head.RecordedAt > now) throw new InvalidDataException();
            if (kind == AuthorityChangeKind.Published)
            {
                if (access.ProposedSnapshot!.ValidUntil <= now) return new(AuthorityMutationStatus.InvalidRequest);
                if (await ExistsAsync(connection, transaction,
                    "SELECT 1 FROM hufu_versions WHERE subject_key=$key AND version=$version", ct,
                    ("$key", key), ("$version", access.ProposedSnapshot.Version)).ConfigureAwait(false))
                    return new(AuthorityMutationStatus.Conflict);
                if (head is not null && head.Context != access.Context)
                {
                    // Contexts may advance under the trusted gate, but an older complete context cannot be restored.
                    if (await ExistsAsync(connection, transaction,
                        "SELECT 1 FROM hufu_events WHERE subject_key=$key AND context_identity=$context", ct,
                        ("$key", key), ("$context", ContextIdentity(access.Context!))).ConfigureAwait(false))
                        return new(AuthorityMutationStatus.Conflict);
                }
            }
            else if (head is not null && head.Context != access.Context) return new(AuthorityMutationStatus.Conflict);
            var record = new AuthorityChangeRecord((head?.Sequence ?? 0) + 1, kind, access.CommandId!, auth.Actor!,
                access.Context!, access.ProposedSnapshot, access.ReasonCode!, now);
            var body = StoreCodec.Encode(StoreCodec.ChangeData.From(record));
            if (!await ReserveAsync(connection, transaction, false, body.Length, ct).ConfigureAwait(false))
                return new(AuthorityMutationStatus.CapacityExceeded);
            await ExecuteAsync(connection, transaction,
                "INSERT INTO hufu_events(subject_key,sequence,context_identity,body,body_hash) VALUES($key,$seq,$context,$body,$hash)", ct,
                ("$key", key), ("$seq", record.Sequence), ("$context", ContextIdentity(record.Context)),
                ("$body", body), ("$hash", StoreCodec.Hash(body))).ConfigureAwait(false);
            if (record.Snapshot is { } snapshot)
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO hufu_versions(subject_key,version,snapshot_identity,sequence) VALUES($key,$version,$identity,$seq)", ct,
                    ("$key", key), ("$version", snapshot.Version), ("$identity", snapshot.Identity), ("$seq", record.Sequence)).ConfigureAwait(false);
            if (head is null)
                await ExecuteAsync(connection, transaction, "INSERT INTO hufu_head(subject_key,sequence) VALUES($key,$seq)", ct,
                    ("$key", key), ("$seq", record.Sequence)).ConfigureAwait(false);
            else if (await ExecuteAsync(connection, transaction,
                "UPDATE hufu_head SET sequence=$seq WHERE subject_key=$key AND sequence=$expected", ct,
                ("$key", key), ("$seq", record.Sequence), ("$expected", head.Sequence)).ConfigureAwait(false) != 1)
                throw new InvalidDataException();
            await SaveCommandAsync(connection, transaction, access.Subject.TenantId, record.CommandId,
                kind == AuthorityChangeKind.Published ? 1 : 2, intent, key, record.Sequence, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            transaction.Commit();
            return new(AuthorityMutationStatus.Applied, record);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return new(AuthorityMutationStatus.Unavailable); }
    }

    public async ValueTask<AuthorityCurrentRead> ReadCurrentAsync(AuthorityStoreActor actor, AuthenticatedAuthorityContext context,
        CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        ct.ThrowIfCancellationRequested();
        if (!AuthorityStoreValidation.ValidActor(actor) || !AuthorityValidation.ValidContext(context)) return new(AuthorityReadStatus.InvalidRequest);
        try
        {
            var auth = await AuthorizeAsync(new(actor, AuthorityStoreOperation.ReadCurrent, AuthoritySubject.From(context), context), ct).ConfigureAwait(false);
            if (auth.Status != AuthorityStatus.Permit) return new(ReadAuthStatus(auth.Status));
            using var connection = await OpenAsync(ct).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: true);
            var head = await HeadAsync(connection, transaction, StoreCodec.SubjectKey(AuthoritySubject.From(context)), ct).ConfigureAwait(false);
            if (head is null) return new(AuthorityReadStatus.NotFound);
            if (head.Context != context) return new(AuthorityReadStatus.StaleContext);
            if (head.Kind == AuthorityChangeKind.Revoked) return new(AuthorityReadStatus.Revoked, head);
            return new(head.Snapshot!.ValidUntil <= _clock.GetUtcNow() ? AuthorityReadStatus.Expired : AuthorityReadStatus.Active, head);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return new(AuthorityReadStatus.Unavailable); }
    }

    public async ValueTask<AuthorityHistoryRead> ReadHistoryAsync(AuthorityStoreActor actor, AuthoritySubject subject,
        int maxEntries = 16, long? beforeSequence = null, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        ct.ThrowIfCancellationRequested();
        if (!AuthorityStoreValidation.ValidActor(actor) || !AuthorityStoreValidation.ValidSubject(subject) ||
            maxEntries is < 1 or > 32 || beforeSequence is <= 0)
            return new(AuthorityReadStatus.InvalidRequest, Array.Empty<AuthorityChangeRecord>());
        try
        {
            var auth = await AuthorizeAsync(new(actor, AuthorityStoreOperation.ReadHistory, subject), ct).ConfigureAwait(false);
            if (auth.Status != AuthorityStatus.Permit) return new(ReadAuthStatus(auth.Status), Array.Empty<AuthorityChangeRecord>());
            using var connection = await OpenAsync(ct).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: true);
            var key = StoreCodec.SubjectKey(subject);
            await HeadAsync(connection, transaction, key, ct).ConfigureAwait(false);
            var (rows, more) = await ChangesAsync(connection, transaction, key, beforeSequence ?? long.MaxValue, maxEntries, ct).ConfigureAwait(false);
            return new(AuthorityReadStatus.Active, rows.AsReadOnly(), more ? rows[^1].Sequence : null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return new(AuthorityReadStatus.Unavailable, Array.Empty<AuthorityChangeRecord>()); }
    }

    public async ValueTask<AuthorityEvidenceWrite> RecordDecisionAsync(AuthorityStoreActor actor, AuthorityDecisionRecord record,
        CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        ct.ThrowIfCancellationRequested();
        if (!AuthorityStoreValidation.ValidActor(actor) || !StoreCodec.ValidDecision(record)) return new(AuthorityEvidenceStatus.InvalidRequest);
        try
        {
            var subject = AuthoritySubject.From(record.Request.Context);
            var access = new AuthorityStoreAccessRequest(actor, AuthorityStoreOperation.RecordDecision, subject,
                record.Request.Context, record.CommandId, DecisionRecord: record);
            var auth = await AuthorizeAsync(access, ct).ConfigureAwait(false);
            if (auth.Status != AuthorityStatus.Permit)
                return new(auth.Status == AuthorityStatus.Deny ? AuthorityEvidenceStatus.Denied : AuthorityEvidenceStatus.Unavailable);
            using var connection = await OpenAsync(ct).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            var key = StoreCodec.SubjectKey(subject);
            var intent = Intent(access, 3);
            var prior = await FindCommandAsync(connection, transaction, subject.TenantId, record.CommandId, ct).ConfigureAwait(false);
            if (prior is not null && (prior.Kind != 3 || prior.Intent != intent || prior.SubjectKey != key))
                return new(AuthorityEvidenceStatus.Conflict);
            var head = await HeadAsync(connection, transaction, key, ct).ConfigureAwait(false);
            var sequenceValue = await ScalarAsync(connection, transaction,
                "SELECT sequence FROM hufu_versions WHERE subject_key=$key AND snapshot_identity=$identity", ct,
                ("$key", key), ("$identity", record.Decision.SnapshotIdentity)).ConfigureAwait(false);
            if (sequenceValue is null) return new(AuthorityEvidenceStatus.StaleAuthority);
            var sequence = Convert.ToInt64(sequenceValue);
            var publication = await LoadChangeAsync(connection, transaction, key, sequence, ct).ConfigureAwait(false);
            var snapshot = publication.Snapshot;
            var now = _clock.GetUtcNow();
            if (record.EvaluatedAt > now) return new(AuthorityEvidenceStatus.InvalidRequest);
            if (snapshot is null || snapshot.Context != record.Request.Context || snapshot.Version != record.Decision.SnapshotVersion ||
                snapshot.Identity != record.Decision.SnapshotIdentity ||
                publication.RecordedAt > record.EvaluatedAt) return new(AuthorityEvidenceStatus.StaleAuthority);
            if (record.Decision.Status == AuthorityStatus.Permit &&
                !PermitStillCurrent(record, head, publication, now))
                return new(AuthorityEvidenceStatus.StaleAuthority);
            if (prior is not null)
            {
                var old = await LoadDecisionAsync(connection, transaction, subject, record.CommandId, ct).ConfigureAwait(false)
                    ?? throw new InvalidDataException();
                if (old.SnapshotSequence != sequence || old.Record != record || old.Actor.ActorId != auth.Actor!.ActorId)
                    throw new InvalidDataException();
                if (record.Decision.Status == AuthorityStatus.Permit &&
                    !PermitStillCurrent(record, head, publication, _clock.GetUtcNow()))
                    return new(AuthorityEvidenceStatus.StaleAuthority);
                return new(AuthorityEvidenceStatus.Replayed, old);
            }
            var entry = new AuthorityDecisionEntry(auth.Actor!, record, sequence, now);
            var body = StoreCodec.Encode(entry);
            if (!await ReserveAsync(connection, transaction, true, body.Length, ct).ConfigureAwait(false))
                return new(AuthorityEvidenceStatus.CapacityExceeded);
            await ExecuteAsync(connection, transaction,
                "INSERT INTO hufu_decisions(tenant_id,command_id,subject_key,body,body_hash) VALUES($tenant,$command,$key,$body,$hash)", ct,
                ("$tenant", subject.TenantId), ("$command", record.CommandId), ("$key", key), ("$body", body), ("$hash", StoreCodec.Hash(body))).ConfigureAwait(false);
            await SaveCommandAsync(connection, transaction, subject.TenantId, record.CommandId, 3, intent, key, sequence, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (record.Decision.Status == AuthorityStatus.Permit &&
                !PermitStillCurrent(record, head, publication, _clock.GetUtcNow()))
                return new(AuthorityEvidenceStatus.StaleAuthority);
            transaction.Commit();
            return new(AuthorityEvidenceStatus.Recorded, entry);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return new(AuthorityEvidenceStatus.Unavailable); }
    }

    public async ValueTask<AuthorityDecisionRead> ReadDecisionAsync(AuthorityStoreActor actor, AuthoritySubject subject,
        string commandId, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        ct.ThrowIfCancellationRequested();
        if (!AuthorityStoreValidation.ValidActor(actor) || !AuthorityStoreValidation.ValidSubject(subject) || !AuthorityValidation.ValidToken(commandId))
            return new(AuthorityReadStatus.InvalidRequest);
        try
        {
            var auth = await AuthorizeAsync(new(actor, AuthorityStoreOperation.ReadDecisions, subject, CommandId: commandId), ct).ConfigureAwait(false);
            if (auth.Status != AuthorityStatus.Permit) return new(ReadAuthStatus(auth.Status));
            using var connection = await OpenAsync(ct).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: true);
            await HeadAsync(connection, transaction, StoreCodec.SubjectKey(subject), ct).ConfigureAwait(false);
            var entry = await LoadDecisionAsync(connection, transaction, subject, commandId, ct).ConfigureAwait(false);
            return new(entry is null ? AuthorityReadStatus.NotFound : AuthorityReadStatus.Active, entry);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return new(AuthorityReadStatus.Unavailable); }
    }

    private async ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(AuthorityStoreAccessRequest access, CancellationToken ct)
    {
        if (access.Actor.TenantId != access.Subject.TenantId) return new(AuthorityStatus.Deny);
        var result = await _authorizer.AuthorizeAsync(access, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (result is null || !Enum.IsDefined(result.Status)) return new(AuthorityStatus.Unavailable);
        if (result.Status == AuthorityStatus.Permit && (!AuthorityStoreValidation.ValidActor(result.Actor) || result.Actor != access.Actor))
            return new(AuthorityStatus.Unavailable);
        return result;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = _databaseOwner is null ? new SqliteConnection(_connectionString!) :
            await _databaseOwner.OpenAsync(ct).ConfigureAwait(false);
        try
        {
            if (_databaseOwner is null) await connection.OpenAsync(ct).ConfigureAwait(false);
            else if (connection.State != System.Data.ConnectionState.Open || string.IsNullOrWhiteSpace(connection.DataSource) ||
                connection.DataSource == ":memory:" || connection.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException();
            connection.DefaultTimeout = 1;
            await RequireSingleFileDatabaseAsync(connection, ct).ConfigureAwait(false);
            await ExecuteAsync(connection, null, "PRAGMA foreign_keys=ON; PRAGMA trusted_schema=OFF; PRAGMA busy_timeout=1000; PRAGMA synchronous=FULL", ct).ConfigureAwait(false);
            using (var transaction = connection.BeginTransaction(deferred: false))
            {
                var id = Convert.ToInt64(await ScalarAsync(connection, transaction, "PRAGMA application_id", ct).ConfigureAwait(false));
                var version = Convert.ToInt64(await ScalarAsync(connection, transaction, "PRAGMA user_version", ct).ConfigureAwait(false));
                if (_databaseOwner is not null)
                {
                    if (!await ExistsAsync(connection, transaction,
                        "SELECT 1 FROM sqlite_schema WHERE type='table' AND name='hufu_meta'", ct).ConfigureAwait(false))
                    {
                        if (await ExistsAsync(connection, transaction,
                            "SELECT 1 FROM sqlite_schema WHERE name GLOB 'hufu_*'", ct).ConfigureAwait(false))
                            throw new InvalidDataException();
                        await ExecuteAsync(connection, transaction, NamespaceSchema, ct).ConfigureAwait(false);
                    }
                }
                else if (id == 0 && version == 0 && !await ExistsAsync(connection, transaction,
                    "SELECT 1 FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%'", ct).ConfigureAwait(false))
                {
                    await ExecuteAsync(connection, transaction, Schema, ct).ConfigureAwait(false);
                }
                else if (id != ApplicationId || version != SchemaVersion) throw new InvalidDataException();
                var codec = await ScalarAsync(connection, transaction, "SELECT codec FROM hufu_meta WHERE id=1", ct).ConfigureAwait(false);
                if (!Equals(codec, "hufu-store-json-v1")) throw new InvalidDataException();
                await ValidateUsageAsync(connection, transaction, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                transaction.Commit();
            }
            var mode = await ScalarAsync(connection, null, "PRAGMA journal_mode=WAL", ct).ConfigureAwait(false);
            if (!string.Equals(mode?.ToString(), "wal", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
            await ExecuteAsync(connection, null, "PRAGMA synchronous=FULL", ct).ConfigureAwait(false);
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private static async Task<AuthorityChangeRecord?> HeadAsync(SqliteConnection connection, SqliteTransaction transaction, string key, CancellationToken ct)
    {
        var current = await ScalarAsync(connection, transaction, "SELECT sequence FROM hufu_head WHERE subject_key=$key", ct, ("$key", key)).ConfigureAwait(false);
        var latest = await ScalarAsync(connection, transaction, "SELECT MAX(sequence) FROM hufu_events WHERE subject_key=$key", ct, ("$key", key)).ConfigureAwait(false);
        if (current is null && latest is null) return null;
        if (current is null || latest is null || Convert.ToInt64(current) < 1 || Convert.ToInt64(current) != Convert.ToInt64(latest))
            throw new InvalidDataException();
        var count = await ScalarAsync(connection, transaction,
            "SELECT COUNT(*) FROM hufu_events WHERE subject_key=$key", ct, ("$key", key)).ConfigureAwait(false);
        if (Convert.ToInt64(count) != Convert.ToInt64(latest)) throw new InvalidDataException();
        return await LoadChangeAsync(connection, transaction, key, Convert.ToInt64(current), ct).ConfigureAwait(false);
    }

    private static async Task RequireSingleFileDatabaseAsync(SqliteConnection connection, CancellationToken ct)
    {
        using (var sql = Command(connection, null, "PRAGMA database_list"))
        using (var reader = await sql.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            var main = false;
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var name = reader.GetString(1);
                if (name == "main")
                {
                    if (main || string.IsNullOrWhiteSpace(reader.GetString(2)) ||
                        Path.GetFullPath(reader.GetString(2)) != Path.GetFullPath(connection.DataSource)) throw new InvalidDataException();
                    main = true;
                }
                else if (name != "temp") throw new InvalidDataException();
            }
            if (!main) throw new InvalidDataException();
        }
        // No TEMP object may shadow the authority/runtime tables in this profile.
        if (await ScalarAsync(connection, null, "SELECT 1 FROM temp.sqlite_schema LIMIT 1", ct).ConfigureAwait(false) is not null)
            throw new InvalidDataException();
    }

    private static async Task<AuthorityChangeRecord> LoadChangeAsync(SqliteConnection connection, SqliteTransaction transaction, string key, long sequence, CancellationToken ct)
    {
        AuthorityChangeRecord record;
        using (var command = Command(connection, transaction,
            "SELECT CASE WHEN typeof(body)='blob' AND length(body)<=$max THEN body END,body_hash,context_identity FROM hufu_events WHERE subject_key=$key AND sequence=$seq",
            ("$max", StoreCodec.MaxBodyBytes), ("$key", key), ("$seq", sequence)))
        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.IsDBNull(0)) throw new InvalidDataException();
            record = StoreCodec.Decode<StoreCodec.ChangeData>((byte[])reader.GetValue(0), reader.GetString(1)).ToRecord();
            if (reader.GetString(2) != ContextIdentity(record.Context)) throw new InvalidDataException();
        }
        if (record.Sequence != sequence || StoreCodec.SubjectKey(AuthoritySubject.From(record.Context)) != key) throw new InvalidDataException();
        var kind = record.Kind == AuthorityChangeKind.Published ? 1 : 2;
        var access = new AuthorityStoreAccessRequest(record.Actor,
            kind == 1 ? AuthorityStoreOperation.Publish : AuthorityStoreOperation.Revoke, AuthoritySubject.From(record.Context),
            record.Context, record.CommandId, record.Sequence - 1, record.Snapshot, record.ReasonCode);
        var commandReceipt = await FindCommandAsync(connection, transaction, record.Context.TenantId, record.CommandId, ct).ConfigureAwait(false);
        if (commandReceipt is null || commandReceipt.Kind != kind || commandReceipt.Sequence != sequence ||
            commandReceipt.SubjectKey != key || commandReceipt.Intent != Intent(access, kind)) throw new InvalidDataException();
        if (record.Snapshot is { } snapshot)
        {
            var versionSequence = await ScalarAsync(connection, transaction,
                "SELECT sequence FROM hufu_versions WHERE subject_key=$key AND version=$version AND snapshot_identity=$identity", ct,
                ("$key", key), ("$version", snapshot.Version), ("$identity", snapshot.Identity)).ConfigureAwait(false);
            if (versionSequence is null || Convert.ToInt64(versionSequence) != sequence) throw new InvalidDataException();
        }
        return record;
    }

    private static async Task<(List<AuthorityChangeRecord> Rows, bool More)> ChangesAsync(SqliteConnection connection, SqliteTransaction transaction,
        string key, long before, int limit, CancellationToken ct)
    {
        var sequences = new List<long>();
        using (var command = Command(connection, transaction,
            "SELECT sequence FROM hufu_events WHERE subject_key=$key AND sequence<$before ORDER BY sequence DESC LIMIT $limit",
            ("$key", key), ("$before", before), ("$limit", limit + 1)))
        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) sequences.Add(reader.GetInt64(0));
        var result = new List<AuthorityChangeRecord>();
        var bytes = 0;
        foreach (var sequence in sequences.Take(limit))
        {
            var length = Convert.ToInt64(await ScalarAsync(connection, transaction,
                "SELECT length(body) FROM hufu_events WHERE subject_key=$key AND sequence=$seq", ct,
                ("$key", key), ("$seq", sequence)).ConfigureAwait(false));
            if (length is < 1 or > StoreCodec.MaxBodyBytes) throw new InvalidDataException();
            if (bytes + length > 4_194_304) break;
            result.Add(await LoadChangeAsync(connection, transaction, key, sequence, ct).ConfigureAwait(false));
            bytes += (int)length;
        }
        return (result, sequences.Count > result.Count);
    }

    private static async Task<AuthorityDecisionEntry?> LoadDecisionAsync(SqliteConnection connection, SqliteTransaction transaction,
        AuthoritySubject subject, string commandId, CancellationToken ct)
    {
        var key = StoreCodec.SubjectKey(subject);
        AuthorityDecisionEntry entry;
        using (var command = Command(connection, transaction,
            "SELECT CASE WHEN typeof(body)='blob' AND length(body)<=$max THEN body END,body_hash FROM hufu_decisions WHERE tenant_id=$tenant AND command_id=$command AND subject_key=$key",
            ("$max", StoreCodec.MaxBodyBytes), ("$tenant", subject.TenantId), ("$command", commandId), ("$key", key)))
        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
            if (reader.IsDBNull(0)) throw new InvalidDataException();
            entry = StoreCodec.Decode<AuthorityDecisionEntry>((byte[])reader.GetValue(0), reader.GetString(1));
        }
        if (!AuthorityStoreValidation.ValidActor(entry.Actor) || !StoreCodec.ValidDecision(entry.Record) ||
            AuthoritySubject.From(entry.Record.Request.Context) != subject || entry.Record.CommandId != commandId ||
            entry.Actor.TenantId != subject.TenantId || entry.SnapshotSequence < 1 || entry.RecordedAt.Offset != TimeSpan.Zero ||
            entry.Record.EvaluatedAt > entry.RecordedAt) throw new InvalidDataException();
        var publication = await LoadChangeAsync(connection, transaction, key, entry.SnapshotSequence, ct).ConfigureAwait(false);
        if (publication.Snapshot is not { } snapshot || snapshot.Context != entry.Record.Request.Context ||
            snapshot.Identity != entry.Record.Decision.SnapshotIdentity || snapshot.Version != entry.Record.Decision.SnapshotVersion ||
            publication.RecordedAt > entry.Record.EvaluatedAt) throw new InvalidDataException();
        var receipt = await FindCommandAsync(connection, transaction, subject.TenantId, commandId, ct).ConfigureAwait(false);
        var access = new AuthorityStoreAccessRequest(entry.Actor, AuthorityStoreOperation.RecordDecision, subject,
            entry.Record.Request.Context, commandId, DecisionRecord: entry.Record);
        if (receipt is null || receipt.Kind != 3 || receipt.Sequence != entry.SnapshotSequence || receipt.SubjectKey != key ||
            receipt.Intent != Intent(access, 3)) throw new InvalidDataException();
        return entry;
    }

    private sealed record CommandReceipt(int Kind, string Intent, string SubjectKey, long Sequence);
    private static async Task<CommandReceipt?> FindCommandAsync(SqliteConnection connection, SqliteTransaction transaction,
        string tenant, string id, CancellationToken ct)
    {
        using var command = Command(connection, transaction,
            "SELECT kind,intent_hash,subject_key,sequence FROM hufu_commands WHERE tenant_id=$tenant AND command_id=$command",
            ("$tenant", tenant), ("$command", id));
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        var receipt = new CommandReceipt(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3));
        if (receipt.Kind is < 1 or > 3 || !StoreCodec.ValidHash(receipt.Intent) || !StoreCodec.ValidHash(receipt.SubjectKey) || receipt.Sequence < 1)
            throw new InvalidDataException();
        return receipt;
    }
    private static Task<int> SaveCommandAsync(SqliteConnection connection, SqliteTransaction transaction, string tenant, string id,
        int kind, string intent, string key, long sequence, CancellationToken ct) => ExecuteAsync(connection, transaction,
            "INSERT INTO hufu_commands(tenant_id,command_id,kind,intent_hash,subject_key,sequence) VALUES($tenant,$command,$kind,$intent,$key,$seq)", ct,
            ("$tenant", tenant), ("$command", id), ("$kind", kind), ("$intent", intent), ("$key", key), ("$seq", sequence));

    private static string Intent(AuthorityStoreAccessRequest access, int kind) => StoreCodec.Hash(StoreCodec.Encode(new
    {
        Domain = "Penghou.Hufu.Command.v1",
        Kind = kind,
        access.Actor.TenantId,
        access.Actor.ActorId,
        access.Subject,
        access.Context,
        access.CommandId,
        access.ExpectedSequence,
        SnapshotIdentity = access.ProposedSnapshot?.Identity,
        access.ReasonCode,
        access.DecisionRecord
    }));
    private static bool PermitStillCurrent(AuthorityDecisionRecord record, AuthorityChangeRecord? head,
        AuthorityChangeRecord publication, DateTimeOffset instant) =>
        instant >= record.EvaluatedAt && publication.Snapshot is { } snapshot &&
        head is { Kind: AuthorityChangeKind.Published } && head.Sequence == publication.Sequence &&
        head.Context == record.Request.Context && snapshot.HasUnchangedValidity(record.EvaluatedAt, instant);
    private static string ContextIdentity(AuthenticatedAuthorityContext context) => StoreCodec.Hash(StoreCodec.Encode(new
    { Domain = "Penghou.Hufu.Context.v1", Context = context }));

    private static async Task ValidateUsageAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        using var command = Command(connection, transaction,
            "SELECT event_count,decision_count,stored_bytes," +
            "(SELECT COUNT(*) FROM hufu_events),(SELECT COUNT(*) FROM hufu_decisions)," +
            "COALESCE((SELECT SUM(length(body)) FROM hufu_events),0)+COALESCE((SELECT SUM(length(body)) FROM hufu_decisions),0) " +
            "FROM hufu_usage WHERE id=1");
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.GetInt64(0) is < 0 or > 100_000 ||
            reader.GetInt64(1) is < 0 or > 1_000_000 || reader.GetInt64(2) is < 0 or > 268_435_456 ||
            reader.GetInt64(0) != reader.GetInt64(3) || reader.GetInt64(1) != reader.GetInt64(4) ||
            reader.GetInt64(2) != reader.GetInt64(5)) throw new InvalidDataException();
    }
    private async Task<bool> ReserveAsync(SqliteConnection connection, SqliteTransaction transaction, bool evidence, int bytes, CancellationToken ct)
    {
        var changed = await ExecuteAsync(connection, transaction,
            "UPDATE hufu_usage SET event_count=event_count+$events,decision_count=decision_count+$decisions,stored_bytes=stored_bytes+$bytes " +
            "WHERE id=1 AND event_count+$events<=$eventMax AND decision_count+$decisions<=$decisionMax AND stored_bytes+$bytes<=$byteMax", ct,
            ("$events", evidence ? 0 : 1), ("$decisions", evidence ? 1 : 0), ("$bytes", bytes),
            ("$eventMax", _options.MaxAuthorityEvents), ("$decisionMax", _options.MaxDecisionEntries), ("$byteMax", _options.MaxStoredBytes)).ConfigureAwait(false);
        return changed == 1;
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object Value)[] values)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value);
        return command;
    }
    private static async Task<int> ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        CancellationToken ct, params (string Name, object Value)[] values)
    {
        ct.ThrowIfCancellationRequested(); using var command = Command(connection, transaction, sql, values);
        var result = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested(); return result;
    }
    private static async Task<object?> ScalarAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        CancellationToken ct, params (string Name, object Value)[] values)
    {
        ct.ThrowIfCancellationRequested(); using var command = Command(connection, transaction, sql, values);
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested();
        return result is null or DBNull ? null : result;
    }
    private static async Task<bool> ExistsAsync(SqliteConnection connection, SqliteTransaction transaction, string sql,
        CancellationToken ct, params (string Name, object Value)[] values) => await ScalarAsync(connection, transaction, sql, ct, values).ConfigureAwait(false) is not null;
    private static AuthorityMutationStatus MutationAuthStatus(AuthorityStatus status) =>
        status == AuthorityStatus.Deny ? AuthorityMutationStatus.Denied : AuthorityMutationStatus.Unavailable;
    private static AuthorityReadStatus ReadAuthStatus(AuthorityStatus status) =>
        status == AuthorityStatus.Deny ? AuthorityReadStatus.Denied : AuthorityReadStatus.Unavailable;
    private static ValueTask<AuthorityMutationResult> InvalidMutation(CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(new AuthorityMutationResult(AuthorityMutationStatus.InvalidRequest)); }

    private const string NamespaceSchema = """
        CREATE TABLE hufu_meta(id INTEGER PRIMARY KEY CHECK(id=1),codec TEXT NOT NULL);
        INSERT INTO hufu_meta VALUES(1,'hufu-store-json-v1');
        CREATE TABLE hufu_events(subject_key TEXT NOT NULL,sequence INTEGER NOT NULL CHECK(sequence>0),context_identity TEXT NOT NULL,body BLOB NOT NULL,body_hash TEXT NOT NULL,PRIMARY KEY(subject_key,sequence));
        CREATE INDEX hufu_event_contexts ON hufu_events(subject_key,context_identity);
        CREATE TABLE hufu_head(subject_key TEXT PRIMARY KEY,sequence INTEGER NOT NULL,FOREIGN KEY(subject_key,sequence) REFERENCES hufu_events(subject_key,sequence));
        CREATE TABLE hufu_versions(subject_key TEXT NOT NULL,version TEXT NOT NULL,snapshot_identity TEXT NOT NULL,sequence INTEGER NOT NULL,PRIMARY KEY(subject_key,version),UNIQUE(subject_key,snapshot_identity),FOREIGN KEY(subject_key,sequence) REFERENCES hufu_events(subject_key,sequence));
        CREATE TABLE hufu_decisions(tenant_id TEXT NOT NULL,command_id TEXT NOT NULL,subject_key TEXT NOT NULL,body BLOB NOT NULL,body_hash TEXT NOT NULL,PRIMARY KEY(tenant_id,command_id));
        CREATE TABLE hufu_commands(tenant_id TEXT NOT NULL,command_id TEXT NOT NULL,kind INTEGER NOT NULL CHECK(kind BETWEEN 1 AND 3),intent_hash TEXT NOT NULL,subject_key TEXT NOT NULL,sequence INTEGER NOT NULL,PRIMARY KEY(tenant_id,command_id));
        CREATE TABLE hufu_usage(id INTEGER PRIMARY KEY CHECK(id=1),event_count INTEGER NOT NULL CHECK(event_count>=0),decision_count INTEGER NOT NULL CHECK(decision_count>=0),stored_bytes INTEGER NOT NULL CHECK(stored_bytes>=0));
        INSERT INTO hufu_usage VALUES(1,0,0,0);
        CREATE TABLE hufu_derivations(derivation_identity TEXT PRIMARY KEY,child_grant_id TEXT NOT NULL UNIQUE,child_tenant TEXT NOT NULL,child_subject TEXT NOT NULL,child_run TEXT NOT NULL,body BLOB NOT NULL,body_hash TEXT NOT NULL,issued_at TEXT NOT NULL);
        CREATE INDEX hufu_derivations_child_grant ON hufu_derivations(child_grant_id);
        CREATE INDEX hufu_derivations_child_subject ON hufu_derivations(child_tenant,child_subject,child_run);
        """;
    private const string Schema = NamespaceSchema + "PRAGMA application_id=1213548117; PRAGMA user_version=2;";
}
