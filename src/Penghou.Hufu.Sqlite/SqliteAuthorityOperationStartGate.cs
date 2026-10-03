using Microsoft.Data.Sqlite;
using System.Text;

namespace Penghou.Hufu.Sqlite;

/// <summary>
/// Trusted registered owner of a single physical local SQLite database.
/// OpenAsync initializes/verifies other schemas and returns a new open connection.
/// No attached database, memory database or path substitution is supported.
/// </summary>
public interface ISqliteAuthorityDatabase
{
    TimeProvider TimeProvider { get; }
    ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Trusted registered runtime participant. Validate the exact binding and acquire
/// runtime state only through the supplied connection/transaction. Never commit,
/// change transaction ownership, open another database, or perform protected I/O.
/// A Permit means the same transaction contains the durable runtime start.
/// </summary>
public interface IAuthoritySqliteStartParticipant
{
    string ProfileIdentity { get; }
    ValueTask<SqliteRuntimeStartResult> TryStartAsync(SqliteConnection connection, SqliteTransaction transaction,
        AuthorityOperationStartCommand command, CancellationToken cancellationToken = default);
}

/// <summary>Runtime facts are held by the transaction; their earliest temporal expiry must still be checked before commit.</summary>
public sealed record SqliteRuntimeStartResult(AuthorityStatus Status, DateTimeOffset ValidUntil);

/// <summary>One co-located authority/runtime start transaction, with no replay dispatch.</summary>
public sealed class SqliteAuthorityOperationStartGate : IAuthorityOperationStartGate
{
    private readonly SqliteAuthorityStore _store;
    private readonly IAuthoritySqliteStartParticipant _participant;
    public SqliteAuthorityOperationStartGate(SqliteAuthorityStore store, IAuthoritySqliteStartParticipant participant)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _participant = participant ?? throw new ArgumentNullException(nameof(participant));
        if (!AuthorityValidation.ValidToken(participant.ProfileIdentity)) throw new ArgumentException("A bounded registered participant profile is required.");
        store.RequireSharedOwner();
    }
    public ValueTask<AuthorityOperationStartResult> StartAsync(AuthorityOperationStartCommand command,
        CancellationToken cancellationToken = default) => _store.StartOperationAsync(command, _participant, cancellationToken);
}

public sealed partial class SqliteAuthorityStore
{
    private static readonly UTF8Encoding StartUtf8 = new(false, true);
    internal void RequireSharedOwner()
    {
        if (_databaseOwner is null) throw new ArgumentException("A registered co-located SQLite database owner is required.");
    }

    internal async ValueTask<AuthorityOperationStartResult> StartOperationAsync(AuthorityOperationStartCommand command,
        IAuthoritySqliteStartParticipant participant, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!ValidStart(command)) return new(AuthorityOperationStartStatus.InvalidRequest);
        try
        {
            var subject = AuthoritySubject.From(command.Request.Context);
            var access = new AuthorityStoreAccessRequest(command.Actor, AuthorityStoreOperation.StartOperation, subject,
                command.Request.Context, command.OperationId, command.ExpectedSequence, StartCommand: command);
            var auth = await AuthorizeAsync(access, ct).ConfigureAwait(false);
            if (auth.Status != AuthorityStatus.Permit)
                return new(auth.Status == AuthorityStatus.Deny ? AuthorityOperationStartStatus.Denied : AuthorityOperationStartStatus.Unavailable);
            using var connection = await OpenAsync(ct).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            await EnsureStartSchemaAsync(connection, transaction, ct).ConfigureAwait(false);
            var key = StoreCodec.SubjectKey(subject);
            var previous = await LoadStartAsync(connection, transaction, subject.TenantId, command.OperationId, ct).ConfigureAwait(false);
            if (previous is not null)
            {
                if (!SameStart(previous, command, participant.ProfileIdentity)) return new(AuthorityOperationStartStatus.Conflict);
                // This is an audit receipt even if authority/runtime state has since moved.
                // It can NEVER report Started or re-invoke the participant.
                return new(AuthorityOperationStartStatus.AlreadyStarted, previous);
            }
            var head = await HeadAsync(connection, transaction, key, ct).ConfigureAwait(false);
            if (head?.Kind != AuthorityChangeKind.Published || head.Sequence != command.ExpectedSequence ||
                head.Context != command.Request.Context) return new(AuthorityOperationStartStatus.StaleAuthority);
            var evidence = await LoadDecisionAsync(connection, transaction, subject, command.DecisionCommandId, ct).ConfigureAwait(false);
            if (evidence is null || evidence.Record.Request != command.Request || evidence.Record.Decision.Status != AuthorityStatus.Permit ||
                evidence.Actor.ActorId != auth.Actor!.ActorId || evidence.SnapshotSequence != head.Sequence ||
                evidence.RecordedAt > _clock.GetUtcNow() || !PermitStillCurrent(evidence.Record, head, head, _clock.GetUtcNow()))
                return new(AuthorityOperationStartStatus.StaleAuthority);
            // A participant is fixed by trusted host composition; agent data cannot select it.
            var runtime = await participant.TryStartAsync(connection, transaction, command, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (runtime is null || !Enum.IsDefined(runtime.Status)) return new(AuthorityOperationStartStatus.Unavailable);
            if (runtime.Status != AuthorityStatus.Permit)
                return new(runtime.Status == AuthorityStatus.Deny ? AuthorityOperationStartStatus.StaleRuntime : AuthorityOperationStartStatus.Unavailable);
            var now = _clock.GetUtcNow();
            if (runtime.ValidUntil.Offset != TimeSpan.Zero || runtime.ValidUntil <= now) return new(AuthorityOperationStartStatus.StaleRuntime);
            if (now < evidence.RecordedAt || !PermitStillCurrent(evidence.Record, head, head, now)) return new(AuthorityOperationStartStatus.StaleAuthority);
            var record = new AuthorityOperationStartRecord(command with { Actor = auth.Actor! }, participant.ProfileIdentity, head.Sequence, now);
            var body = StoreCodec.Encode(record);
            if (await ExecuteAsync(connection, transaction,
                "UPDATE hufu_start_usage SET entry_count=entry_count+1,stored_bytes=stored_bytes+$bytes " +
                "WHERE id=1 AND entry_count<$max AND stored_bytes+$bytes<=$byteMax", ct,
                ("$bytes", body.Length), ("$max", _options.MaxDecisionEntries), ("$byteMax", _options.MaxStoredBytes)).ConfigureAwait(false) != 1)
                return new(AuthorityOperationStartStatus.CapacityExceeded);
            await ExecuteAsync(connection, transaction,
                "INSERT INTO hufu_operation_starts(tenant_id,operation_id,body,body_hash) VALUES($tenant,$operation,$body,$hash)", ct,
                ("$tenant", subject.TenantId), ("$operation", command.OperationId), ("$body", body), ("$hash", StoreCodec.Hash(body))).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var commitAt = _clock.GetUtcNow();
            if (runtime.ValidUntil <= commitAt) return new(AuthorityOperationStartStatus.StaleRuntime);
            if (!PermitStillCurrent(evidence.Record, head, head, commitAt)) return new(AuthorityOperationStartStatus.StaleAuthority);
            transaction.Commit();
            return new(AuthorityOperationStartStatus.Started, record);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return new(AuthorityOperationStartStatus.Unavailable); }
    }

    private static bool ValidStart(AuthorityOperationStartCommand? command)
    {
        if (command is null || !AuthorityStoreValidation.ValidActor(command.Actor) ||
            !AuthorityValidation.ValidToken(command.OperationId) || !AuthorityValidation.IsValidRequest(command.Request) ||
            !AuthorityValidation.ValidToken(command.DecisionCommandId) || command.ExpectedSequence < 1 ||
            !StoreCodec.ValidHash(command.BindingIdentity) || command.BindingJson is null) return false;
        try
        {
            return StoreCodec.ValidEvidenceJson(command.BindingJson) && StoreCodec.Hash(StartUtf8.GetBytes(command.BindingJson)) == command.BindingIdentity;
        }
        catch { return false; }
    }
    private static bool SameStart(AuthorityOperationStartRecord old, AuthorityOperationStartCommand command, string profile) =>
        old.ParticipantProfile == profile && (old.Command with { Actor = command.Actor }) == command &&
        old.Command.Actor.TenantId == command.Actor.TenantId && old.Command.Actor.ActorId == command.Actor.ActorId;

    private static async Task<AuthorityOperationStartRecord?> LoadStartAsync(SqliteConnection connection, SqliteTransaction transaction,
        string tenant, string operation, CancellationToken ct)
    {
        AuthorityOperationStartRecord record;
        using (var sql = Command(connection, transaction,
            "SELECT CASE WHEN typeof(body)='blob' AND length(body)<=$max THEN body END,body_hash FROM hufu_operation_starts WHERE tenant_id=$tenant AND operation_id=$operation",
            ("$max", StoreCodec.MaxBodyBytes), ("$tenant", tenant), ("$operation", operation)))
        using (var reader = await sql.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
            if (reader.IsDBNull(0)) throw new InvalidDataException();
            record = StoreCodec.Decode<AuthorityOperationStartRecord>((byte[])reader.GetValue(0), reader.GetString(1));
        }
        if (!ValidStart(record.Command) || record.Command.Actor.TenantId != tenant || record.Command.OperationId != operation ||
            record.SnapshotSequence != record.Command.ExpectedSequence || !AuthorityValidation.ValidToken(record.ParticipantProfile) ||
            record.StartedAt.Offset != TimeSpan.Zero) throw new InvalidDataException();
        var evidence = await LoadDecisionAsync(connection, transaction, AuthoritySubject.From(record.Command.Request.Context),
            record.Command.DecisionCommandId, ct).ConfigureAwait(false);
        if (evidence is null || evidence.SnapshotSequence != record.SnapshotSequence || evidence.Record.Request != record.Command.Request ||
            evidence.Record.Decision.Status != AuthorityStatus.Permit || evidence.Actor.ActorId != record.Command.Actor.ActorId ||
            evidence.RecordedAt > record.StartedAt) throw new InvalidDataException();
        return record;
    }
    private static async Task EnsureStartSchemaAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        if (!await ExistsAsync(connection, transaction,
            "SELECT 1 FROM sqlite_schema WHERE type='table' AND name='hufu_start_meta'", ct).ConfigureAwait(false))
        {
            if (await ExistsAsync(connection, transaction,
                "SELECT 1 FROM sqlite_schema WHERE name IN ('hufu_operation_starts','hufu_start_usage')", ct).ConfigureAwait(false)) throw new InvalidDataException();
            await ExecuteAsync(connection, transaction, """
                CREATE TABLE hufu_start_meta(id INTEGER PRIMARY KEY CHECK(id=1),codec TEXT NOT NULL);
                INSERT INTO hufu_start_meta VALUES(1,'hufu-start-json-v1');
                CREATE TABLE hufu_operation_starts(tenant_id TEXT NOT NULL,operation_id TEXT NOT NULL,body BLOB NOT NULL,body_hash TEXT NOT NULL,PRIMARY KEY(tenant_id,operation_id));
                CREATE TABLE hufu_start_usage(id INTEGER PRIMARY KEY CHECK(id=1),entry_count INTEGER NOT NULL CHECK(entry_count>=0),stored_bytes INTEGER NOT NULL CHECK(stored_bytes>=0));
                INSERT INTO hufu_start_usage VALUES(1,0,0);
                """, ct).ConfigureAwait(false);
        }
        if (!Equals(await ScalarAsync(connection, transaction, "SELECT codec FROM hufu_start_meta WHERE id=1", ct).ConfigureAwait(false), "hufu-start-json-v1"))
            throw new InvalidDataException();
        using var sql = Command(connection, transaction,
            "SELECT entry_count,stored_bytes,(SELECT COUNT(*) FROM hufu_operation_starts)," +
            "COALESCE((SELECT SUM(length(body)) FROM hufu_operation_starts),0) FROM hufu_start_usage WHERE id=1");
        using var reader = await sql.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.GetInt64(0) is < 0 or > 1_000_000 ||
            reader.GetInt64(1) is < 0 or > 268_435_456 || reader.GetInt64(0) != reader.GetInt64(2) || reader.GetInt64(1) != reader.GetInt64(3))
            throw new InvalidDataException();
    }
}
