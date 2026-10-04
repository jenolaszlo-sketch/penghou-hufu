using System.Data;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Penghou.Hufu.Sqlite;
using Penghou.IO.Abstractions;
using Penghou.Luban.Execution;

[assembly: InternalsVisibleTo("Penghou.Hufu.Luban.Sqlite.Tests")]

namespace Penghou.Hufu.Luban.Sqlite;

/// <summary>
/// Co-located approval, patch-start participant, and outcome journal. The host
/// remains responsible for protecting the database file from untrusted writers.
/// </summary>
public sealed class SqlitePatchOutcomeJournal : ISqliteAuthorityDatabase, IAuthoritySqliteStartParticipant
{
    private const string Codec = "hufu-patch-journal-json-v2";
    private const int SchemaVersion = 1;
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly IPatchJournalAuthorizer _authorizer;
    private readonly PatchJournalOptions _options;

    public SqlitePatchOutcomeJournal(string databasePath, IPatchJournalAuthorizer authorizer,
        TimeProvider? clock = null, PatchJournalOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (databasePath == ":memory:" || databasePath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A disk-backed single-file SQLite database is required.", nameof(databasePath));
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        TimeProvider = clock ?? TimeProvider.System;
        _options = options ?? new PatchJournalOptions();
        if (_options.MaxEntries is < 1 or > 100_000 || _options.MaxStoredBytes < PatchIdentity.MaxRecordBytes ||
            _options.MaxStoredBytes > 268_435_456)
            throw new ArgumentOutOfRangeException(nameof(options));
        // Keep the owner's DataSource canonical as well as its identity checks.
        // macOS temporary paths commonly traverse /var -> /private/var; the
        // generic authority owner also compares SQLite's resolved main path.
        _databasePath = PhysicalPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 1
        }.ToString();
    }

    public TimeProvider TimeProvider { get; }
    public string ProfileIdentity => PatchIdentity.ParticipantProfile;

    public async ValueTask<PatchApprovalResult> ApproveAsync(PatchApprovalCommand command,
        CancellationToken cancellationToken = default)
    {
        if (!ValidApprovalCommand(command)) return new(PatchApprovalStatus.InvalidRequest);
        string admissionIdentity;
        PatchApprovedIntent intent;
        try
        {
            admissionIdentity = PatchIdentity.Admission(command.Context, command.Admission);
            intent = ApprovedIntent(command.Context, command.Admission);
        }
        catch { return new(PatchApprovalStatus.InvalidRequest); }
        var auth = await AuthorizeAsync(new(command.Actor, command.Context, PatchJournalOperation.Approve,
            command.Admission.OperationId, admissionIdentity, command.CommandId, Approval: command), cancellationToken).ConfigureAwait(false);
        if (auth != AuthorityStatus.Permit) return new(AuthApprovalStatus(auth));
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            await ValidateSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var prior = await ReadApprovalAsync(connection, transaction, command.Actor.TenantId,
                command.Admission.OperationId, cancellationToken).ConfigureAwait(false);
            if (prior is not null)
            {
                var same = prior.CommandId == command.CommandId && SameActor(prior.Actor, command.Actor) &&
                    prior.Context == command.Context && prior.OperationId == command.Admission.OperationId &&
                    prior.AdmissionIdentity == admissionIdentity && prior.ExpiresAt == command.ExpiresAt && prior.Intent == intent;
                return new(same ? PatchApprovalStatus.Replayed : PatchApprovalStatus.Conflict, prior);
            }
            if (command.ExpiresAt <= TimeProvider.GetUtcNow()) return new(PatchApprovalStatus.Expired);
            if (await ReadOutcomeAsync(connection, transaction, command.Actor.TenantId,
                command.Admission.OperationId, cancellationToken).ConfigureAwait(false) is not null)
                return new(PatchApprovalStatus.Conflict);
            var record = new PatchApprovalRecord(command.CommandId, command.Actor, command.Context,
                command.Admission.OperationId, admissionIdentity, command.ExpiresAt, intent);
            if (!await ReserveCapacityAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
                return new(PatchApprovalStatus.CapacityExceeded);
            await InsertApprovalAsync(connection, transaction, record, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return new(PatchApprovalStatus.Recorded, record);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return new(PatchApprovalStatus.Unavailable); }
    }

    public async ValueTask<PatchApprovalResult> RevokeApprovalAsync(AuthorityStoreActor actor,
        AuthenticatedAuthorityContext context, string operationId, string admissionIdentity, string commandId,
        CancellationToken cancellationToken = default)
    {
        if (!ValidKey(actor, context, operationId, admissionIdentity) || !AuthorityValidation.ValidToken(commandId))
            return new(PatchApprovalStatus.InvalidRequest);
        var auth = await AuthorizeAsync(new(actor, context, PatchJournalOperation.RevokeApproval,
            operationId, admissionIdentity, commandId), cancellationToken).ConfigureAwait(false);
        if (auth != AuthorityStatus.Permit) return new(AuthApprovalStatus(auth));
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            await ValidateSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var prior = await ReadApprovalAsync(connection, transaction, actor.TenantId, operationId, cancellationToken).ConfigureAwait(false);
            if (prior is null) return new(PatchApprovalStatus.NotFound);
            if (prior.Context != context || prior.AdmissionIdentity != admissionIdentity) return new(PatchApprovalStatus.Conflict);
            if (prior.Revoked)
            {
                var replay = prior.RevokeCommandId == commandId && prior.RevokedBy is { } revokedBy && SameActor(revokedBy, actor);
                return new(replay ? PatchApprovalStatus.Replayed : PatchApprovalStatus.Conflict, prior);
            }
            var updated = prior with { Revoked = true, RevokeCommandId = commandId, RevokedBy = actor };
            await UpdateApprovalAsync(connection, transaction, prior, updated, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return new(PatchApprovalStatus.Revoked, updated);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return new(PatchApprovalStatus.Unavailable); }
    }

    public async ValueTask<PatchApprovalResult> ReadApprovalAsync(AuthorityStoreActor actor,
        AuthenticatedAuthorityContext context, string operationId, string admissionIdentity,
        CancellationToken cancellationToken = default)
    {
        if (!ValidKey(actor, context, operationId, admissionIdentity)) return new(PatchApprovalStatus.InvalidRequest);
        var auth = await AuthorizeAsync(new(actor, context, PatchJournalOperation.ReadApproval,
            operationId, admissionIdentity), cancellationToken).ConfigureAwait(false);
        if (auth != AuthorityStatus.Permit) return new(AuthApprovalStatus(auth));
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: true);
            await ValidateSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var record = await ReadApprovalAsync(connection, transaction, actor.TenantId, operationId, cancellationToken).ConfigureAwait(false);
            if (record is null) return new(PatchApprovalStatus.NotFound);
            if (record.Context != context || record.AdmissionIdentity != admissionIdentity) return new(PatchApprovalStatus.NotFound);
            var status = record.Revoked ? PatchApprovalStatus.Revoked :
                record.ExpiresAt <= TimeProvider.GetUtcNow() ? PatchApprovalStatus.Expired : PatchApprovalStatus.Active;
            return new(status, record);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return new(PatchApprovalStatus.Unavailable); }
    }

    public async ValueTask<bool> ReserveAsync(PatchOutcomeRecord record, CancellationToken cancellationToken = default)
    {
        if (!PatchIdentity.ValidRecord(record) || record.State != PatchRecoveryState.Reserved ||
            record.EvidenceId is not null || record.ObservedVersion is not null) return false;
        var auth = await AuthorizeAsync(new(record.Actor, record.Context, PatchJournalOperation.Reserve,
            record.OperationId, record.AdmissionIdentity, Outcome: record), cancellationToken).ConfigureAwait(false);
        if (auth != AuthorityStatus.Permit) return false;
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            await ValidateSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var approval = await ReadApprovalAsync(connection, transaction, record.Actor.TenantId,
                record.OperationId, cancellationToken).ConfigureAwait(false);
            if (approval is null || !MatchesApproval(approval, record) || approval.Revoked || approval.ExpiresAt <= TimeProvider.GetUtcNow()) return false;
            if (await ReadOutcomeAsync(connection, transaction, record.Actor.TenantId,
                record.OperationId, cancellationToken).ConfigureAwait(false) is not null) return false;
            if (!await ReserveCapacityAsync(connection, transaction, cancellationToken).ConfigureAwait(false)) return false;
            await InsertOutcomeAsync(connection, transaction, record, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    public async ValueTask<PatchOutcomeRead> InspectAsync(AuthorityStoreActor actor,
        AuthenticatedAuthorityContext context, string operationId, string admissionIdentity,
        CancellationToken cancellationToken = default)
    {
        if (!ValidKey(actor, context, operationId, admissionIdentity)) return new(PatchRecoveryState.Unavailable);
        var auth = await AuthorizeAsync(new(actor, context, PatchJournalOperation.ReadOutcome,
            operationId, admissionIdentity), cancellationToken).ConfigureAwait(false);
        if (auth != AuthorityStatus.Permit) return new(PatchRecoveryState.Unavailable);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: true);
            await ValidateSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var record = await ReadOutcomeAsync(connection, transaction, actor.TenantId, operationId, cancellationToken).ConfigureAwait(false);
            if (record is null) return new(PatchRecoveryState.NotFound);
            if (record.Context != context || record.AdmissionIdentity != admissionIdentity || !SameActor(record.Actor, actor))
                return new(PatchRecoveryState.Unavailable);
            var approval = await ReadApprovalAsync(connection, transaction, actor.TenantId, operationId, cancellationToken).ConfigureAwait(false);
            if (approval is null || !MatchesApproval(approval, record)) return new(PatchRecoveryState.Unavailable);
            return new(record.State, record);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return new(PatchRecoveryState.Unavailable); }
    }

    public async ValueTask<bool> CompleteAsync(PatchOutcomeRecord expected, MutationCompletion completion,
        CancellationToken cancellationToken = default)
    {
        if (!PatchIdentity.ValidRecord(expected) || expected.State != PatchRecoveryState.Started || completion is null ||
            !Enum.IsDefined(completion.Outcome) || completion.Start != expected.Start || completion.EvidenceId != expected.EvidenceId ||
            completion.Outcome == MutationOutcome.Completed && completion.ObservedVersion != expected.Start.ProposedVersion ||
            completion.Outcome == MutationOutcome.NoMutation && completion.ObservedVersion is { } observed && observed != expected.Start.OriginalVersion ||
            completion.Outcome == MutationOutcome.Ambiguous && completion.ObservedVersion is { } ambiguous && !IsVersion(ambiguous)) return false;
        var auth = await AuthorizeAsync(new(expected.Actor, expected.Context, PatchJournalOperation.Complete,
            expected.OperationId, expected.AdmissionIdentity, Outcome: expected, Completion: completion), cancellationToken).ConfigureAwait(false);
        if (auth != AuthorityStatus.Permit) return false;
        var terminalState = completion.Outcome switch
        {
            MutationOutcome.Completed => PatchRecoveryState.Completed,
            MutationOutcome.NoMutation => PatchRecoveryState.NoMutation,
            _ => PatchRecoveryState.Ambiguous
        };
        var updated = expected with { State = terminalState, ObservedVersion = completion.ObservedVersion };
        if (!PatchIdentity.ValidRecord(updated)) return false;
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            await ValidateSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var current = await ReadOutcomeAsync(connection, transaction, expected.Actor.TenantId,
                expected.OperationId, cancellationToken).ConfigureAwait(false);
            if (current is null || current.Context != expected.Context || current.AdmissionIdentity != expected.AdmissionIdentity ||
                !SameActor(current.Actor, expected.Actor)) return false;
            var approval = await ReadApprovalAsync(connection, transaction, expected.Actor.TenantId,
                expected.OperationId, cancellationToken).ConfigureAwait(false);
            if (approval is null || !MatchesApproval(approval, current)) return false;
            if (current == updated) return true;
            if (current != expected) return false;
            await UpdateOutcomeAsync(connection, transaction, current, updated, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    public async ValueTask<bool> ReconcileAmbiguousAsync(AuthorityStoreActor actor,
        AuthenticatedAuthorityContext context, string operationId, string admissionIdentity,
        CancellationToken cancellationToken = default)
    {
        if (!ValidKey(actor, context, operationId, admissionIdentity)) return false;
        var auth = await AuthorizeAsync(new(actor, context, PatchJournalOperation.ReconcileAmbiguous,
            operationId, admissionIdentity), cancellationToken).ConfigureAwait(false);
        if (auth != AuthorityStatus.Permit) return false;
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            await ValidateSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var current = await ReadOutcomeAsync(connection, transaction, actor.TenantId, operationId, cancellationToken).ConfigureAwait(false);
            if (current is null || current.Context != context || current.AdmissionIdentity != admissionIdentity || !SameActor(current.Actor, actor)) return false;
            var approval = await ReadApprovalAsync(connection, transaction, actor.TenantId, operationId, cancellationToken).ConfigureAwait(false);
            if (approval is null || !MatchesApproval(approval, current)) return false;
            if (current.State == PatchRecoveryState.Ambiguous) return true;
            if (current.State is not (PatchRecoveryState.Reserved or PatchRecoveryState.Started)) return false;
            var updated = current with
            {
                State = PatchRecoveryState.Ambiguous,
                EvidenceId = current.EvidenceId ?? PatchIdentity.Evidence(current),
                ObservedVersion = null
            };
            if (!PatchIdentity.ValidRecord(updated)) return false;
            await UpdateOutcomeAsync(connection, transaction, current, updated, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    /// <summary>Opens and initializes this owner's schema on the configured physical database.</summary>
    public async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            connection.DefaultTimeout = 1;
            await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            if (!await IsSameDatabaseAsync(connection, cancellationToken).ConfigureAwait(false)) throw new InvalidDataException("Unexpected SQLite database path.");
            using (var transaction = connection.BeginTransaction(deferred: false))
            {
                await EnsureSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                await ValidateSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                transaction.Commit();
            }
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Registered with the authority start gate. This callback uses only the
    /// supplied owner connection and transaction, and never commits either.
    /// </summary>
    public async ValueTask<SqliteRuntimeStartResult> TryStartAsync(SqliteConnection connection,
        SqliteTransaction transaction, AuthorityOperationStartCommand command,
        CancellationToken cancellationToken = default)
    {
        if (connection is null || transaction is null || command is null ||
            connection.State != ConnectionState.Open || !ReferenceEquals(transaction.Connection, connection) ||
            command.Request.Context is null || !AuthorityValidation.ValidContext(command.Request.Context) ||
            !AuthorityStoreValidation.ValidActor(command.Actor) || command.Actor.TenantId != command.Request.Context.TenantId)
            return new(AuthorityStatus.Deny, DateTimeOffset.MinValue);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await IsSameDatabaseAsync(connection, cancellationToken, transaction).ConfigureAwait(false))
                return new(AuthorityStatus.Deny, DateTimeOffset.MinValue);
            await ValidateSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var record = await ReadOutcomeAsync(connection, transaction, command.Actor.TenantId,
                command.OperationId, cancellationToken).ConfigureAwait(false);
            if (record is null || record.State != PatchRecoveryState.Reserved || !SameActor(record.Actor, command.Actor) ||
                record.Context != command.Request.Context || command != PatchIdentity.Command(record))
                return new(AuthorityStatus.Deny, DateTimeOffset.MinValue);
            var approval = await ReadApprovalAsync(connection, transaction, record.Actor.TenantId,
                record.OperationId, cancellationToken).ConfigureAwait(false);
            var now = TimeProvider.GetUtcNow();
            if (approval is null || !MatchesApproval(approval, record) || approval.Revoked || approval.ExpiresAt <= now)
                return new(AuthorityStatus.Deny, DateTimeOffset.MinValue);
            var started = record with { State = PatchRecoveryState.Started, EvidenceId = PatchIdentity.Evidence(record) };
            if (!PatchIdentity.ValidRecord(started)) return new(AuthorityStatus.Unavailable, DateTimeOffset.MinValue);
            await UpdateOutcomeAsync(connection, transaction, record, started, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new(AuthorityStatus.Permit, approval.ExpiresAt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return new(AuthorityStatus.Unavailable, DateTimeOffset.MinValue); }
    }

    private async ValueTask<AuthorityStatus> AuthorizeAsync(PatchJournalAccessRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var result = await _authorizer.AuthorizeAsync(request, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (result is null || !Enum.IsDefined(result.Status)) return AuthorityStatus.Unavailable;
            if (result.Status == AuthorityStatus.Permit)
                return AuthorityStoreValidation.ValidActor(result.Actor) && result.Actor == request.Actor
                    ? AuthorityStatus.Permit : AuthorityStatus.Unavailable;
            return result.Status;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return AuthorityStatus.Unavailable; }
    }

    private static PatchApprovalStatus AuthApprovalStatus(AuthorityStatus status) => status == AuthorityStatus.Deny
        ? PatchApprovalStatus.Denied : PatchApprovalStatus.Unavailable;

    private static bool ValidApprovalCommand(PatchApprovalCommand? command) => command is not null &&
        AuthorityStoreValidation.ValidActor(command.Actor) && AuthorityValidation.ValidContext(command.Context) &&
        command.Actor.TenantId == command.Context.TenantId && AuthorityValidation.ValidToken(command.CommandId) &&
        command.ExpiresAt.Offset == TimeSpan.Zero && PatchIdentity.Valid(command.Context, command.Admission);

    private static bool ValidKey(AuthorityStoreActor? actor, AuthenticatedAuthorityContext? context,
        string? operationId, string? admissionIdentity) => AuthorityStoreValidation.ValidActor(actor) &&
        AuthorityValidation.ValidContext(context) && actor!.TenantId == context!.TenantId &&
        AuthorityValidation.ValidToken(operationId) && PatchIdentity.IsHash(admissionIdentity);

    private static bool SameActor(AuthorityStoreActor left, AuthorityStoreActor right) =>
        left.TenantId == right.TenantId && left.ActorId == right.ActorId;

    private static bool MatchesApproval(PatchApprovalRecord approval, PatchOutcomeRecord outcome) =>
        approval.Context == outcome.Context && approval.OperationId == outcome.OperationId &&
        approval.AdmissionIdentity == outcome.AdmissionIdentity && SameActor(approval.Actor, outcome.Actor) &&
        approval.Intent == IntentOf(outcome);

    private async ValueTask ConfigureConnectionAsync(SqliteConnection connection, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA trusted_schema=OFF; PRAGMA busy_timeout=1000; PRAGMA synchronous=FULL";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        command.CommandText = "PRAGMA journal_mode=WAL";
        var mode = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (!string.Equals(Convert.ToString(mode), "wal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("WAL mode is required.");
    }

    private async ValueTask<bool> IsSameDatabaseAsync(SqliteConnection connection, CancellationToken ct,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA database_list";
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var mainCount = 0;
        var tempCount = 0;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var name = reader.GetString(1);
            if (string.Equals(name, "main", StringComparison.Ordinal))
            {
                mainCount++;
                if (reader.IsDBNull(2)) return false;
                var actual = reader.GetString(2);
                if (string.IsNullOrWhiteSpace(actual) || actual == ":memory:" || actual.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return false;
                if (!string.Equals(PhysicalPath(actual), PhysicalPath(_databasePath), OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return false;
            }
            else if (string.Equals(name, "temp", StringComparison.Ordinal)) tempCount++;
            else return false;
        }
        if (mainCount != 1 || tempCount > 1) return false;

        // SQLite may create an empty temp schema as a side effect of a trusted
        // owner checking database_list. Permit that empty schema only. Every
        // journal query also names main explicitly, preventing name shadowing.
        using var temp = connection.CreateCommand();
        temp.Transaction = transaction;
        temp.CommandText = "SELECT 1 FROM temp.sqlite_schema LIMIT 1";
        return await temp.ExecuteScalarAsync(ct).ConfigureAwait(false) is null;
    }

    private static string PhysicalPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? throw new IOException("Database path has no root.");
        var current = root;
        foreach (var component in full[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, component);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            var target = info.Exists ? info.ResolveLinkTarget(returnFinalTarget: true) : null;
            current = target is null ? next : Path.GetFullPath(target.FullName);
        }
        return Path.GetFullPath(current);
    }

    private async ValueTask EnsureSchemaAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        if (!await ExistsAsync(connection, transaction,
                "SELECT 1 FROM main.sqlite_schema WHERE name='hp_meta'", ct).ConfigureAwait(false))
        {
            if (await ExistsAsync(connection, transaction, "SELECT 1 FROM main.sqlite_schema WHERE name GLOB 'hp_*'", ct).ConfigureAwait(false))
                throw new InvalidDataException("Unknown patch journal schema exists.");
            using var create = connection.CreateCommand();
            create.Transaction = transaction;
            create.CommandText = """
                CREATE TABLE main.hp_meta(id INTEGER PRIMARY KEY CHECK(id=1), codec TEXT NOT NULL, schema_version INTEGER NOT NULL);
                INSERT INTO main.hp_meta VALUES(1,'hufu-patch-journal-json-v2',1);
                CREATE TABLE main.hp_usage(id INTEGER PRIMARY KEY CHECK(id=1), entry_count INTEGER NOT NULL CHECK(entry_count>=0), reserved_bytes INTEGER NOT NULL CHECK(reserved_bytes>=0));
                INSERT INTO main.hp_usage VALUES(1,0,0);
                CREATE TABLE main.hp_approvals(tenant_id TEXT NOT NULL, operation_id TEXT NOT NULL, body BLOB NOT NULL, body_hash TEXT NOT NULL, reserved_bytes INTEGER NOT NULL CHECK(reserved_bytes=32768), PRIMARY KEY(tenant_id,operation_id));
                CREATE TABLE main.hp_outcomes(tenant_id TEXT NOT NULL, operation_id TEXT NOT NULL, body BLOB NOT NULL, body_hash TEXT NOT NULL, reserved_bytes INTEGER NOT NULL CHECK(reserved_bytes=32768), PRIMARY KEY(tenant_id,operation_id));
                """;
            await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private async ValueTask ValidateSchemaAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken ct)
    {
        var codec = await ScalarAsync(connection, transaction,
            "SELECT codec || ':' || schema_version FROM main.hp_meta WHERE id=1", ct).ConfigureAwait(false);
        using (var objects = connection.CreateCommand())
        {
            objects.Transaction = transaction;
            objects.CommandText = "SELECT name FROM main.sqlite_schema WHERE name GLOB 'hp_*' ORDER BY name";
            using var rows = await objects.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var names = new List<string>();
            while (await rows.ReadAsync(ct).ConfigureAwait(false)) names.Add(rows.GetString(0));
            if (!names.SequenceEqual(new[] { "hp_approvals", "hp_meta", "hp_outcomes", "hp_usage" }, StringComparer.Ordinal))
                throw new InvalidDataException("Unknown patch journal objects exist.");
        }
        if (!Equals(codec, Codec + ":" + SchemaVersion)) throw new InvalidDataException("Unsupported patch journal schema.");
        if (await ExistsAsync(connection, transaction,
                "SELECT 1 FROM main.sqlite_schema WHERE name NOT GLOB 'sqlite_*' AND name NOT GLOB 'hufu_*' AND name NOT GLOB 'hp_*' LIMIT 1", ct)
            .ConfigureAwait(false)) throw new InvalidDataException("Unexpected application schema exists.");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT u.entry_count,u.reserved_bytes,
              (SELECT COUNT(*) FROM main.hp_approvals)+(SELECT COUNT(*) FROM main.hp_outcomes),
              COALESCE((SELECT SUM(reserved_bytes) FROM main.hp_approvals),0)+COALESCE((SELECT SUM(reserved_bytes) FROM main.hp_outcomes),0),
              (SELECT COUNT(*) FROM main.hp_approvals WHERE reserved_bytes<>$row),
              (SELECT COUNT(*) FROM main.hp_outcomes WHERE reserved_bytes<>$row)
            FROM main.hp_usage u WHERE u.id=1
            """;
        command.Parameters.AddWithValue("$row", PatchIdentity.MaxRecordBytes);
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.GetInt64(0) < 0 || reader.GetInt64(0) > _options.MaxEntries ||
            reader.GetInt64(1) < 0 || reader.GetInt64(1) > _options.MaxStoredBytes ||
            reader.GetInt64(0) != reader.GetInt64(2) || reader.GetInt64(1) != reader.GetInt64(3) ||
            reader.GetInt64(4) != 0 || reader.GetInt64(5) != 0) throw new InvalidDataException("Inconsistent patch journal capacity ledger.");
    }

    private async ValueTask<bool> ReserveCapacityAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        await ValidateSchemaAsync(connection, transaction, ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE main.hp_usage SET entry_count=entry_count+1,reserved_bytes=reserved_bytes+$row WHERE id=1 AND entry_count<$maxEntries AND reserved_bytes+$row<=$maxBytes";
        command.Parameters.AddWithValue("$row", PatchIdentity.MaxRecordBytes);
        command.Parameters.AddWithValue("$maxEntries", _options.MaxEntries);
        command.Parameters.AddWithValue("$maxBytes", _options.MaxStoredBytes);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    private static async ValueTask<PatchApprovalRecord?> ReadApprovalAsync(SqliteConnection connection,
        SqliteTransaction? transaction, string tenant, string operation, CancellationToken ct)
    {
        var bytes = await ReadBodyAsync(connection, transaction, "hp_approvals", tenant, operation, ct).ConfigureAwait(false);
        if (bytes is null) return null;
        var record = Decode<PatchApprovalRecord>(bytes.Value.Body, bytes.Value.Hash);
        if (!ValidApprovalRecord(record) || record.Actor.TenantId != tenant || record.OperationId != operation) throw new InvalidDataException();
        return record;
    }

    private static async ValueTask<PatchOutcomeRecord?> ReadOutcomeAsync(SqliteConnection connection,
        SqliteTransaction? transaction, string tenant, string operation, CancellationToken ct)
    {
        var bytes = await ReadBodyAsync(connection, transaction, "hp_outcomes", tenant, operation, ct).ConfigureAwait(false);
        if (bytes is null) return null;
        var record = Decode<PatchOutcomeRecord>(bytes.Value.Body, bytes.Value.Hash);
        if (!PatchIdentity.ValidRecord(record) || record.Actor.TenantId != tenant || record.OperationId != operation) throw new InvalidDataException();
        return record;
    }

    private static async ValueTask<(byte[] Body, string Hash)?> ReadBodyAsync(SqliteConnection connection,
        SqliteTransaction? transaction, string table, string tenant, string operation, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT CASE WHEN typeof(body)='blob' AND length(body)<=$max THEN body END,body_hash FROM main.{table} WHERE tenant_id=$tenant AND operation_id=$operation";
        command.Parameters.AddWithValue("$max", PatchIdentity.MaxRecordBytes);
        command.Parameters.AddWithValue("$tenant", tenant);
        command.Parameters.AddWithValue("$operation", operation);
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        if (reader.IsDBNull(0) || reader.IsDBNull(1)) throw new InvalidDataException("Invalid bounded journal body.");
        return ((byte[])reader.GetValue(0), reader.GetString(1));
    }

    private static T Decode<T>(byte[] body, string expectedHash)
    {
        if (body.Length > PatchIdentity.MaxRecordBytes || !FixedHash(body, expectedHash)) throw new InvalidDataException("Journal checksum failed.");
        var value = JsonSerializer.Deserialize<T>(body, PatchIdentity.Json) ?? throw new InvalidDataException("Invalid journal record.");
        if (!Encode(value).AsSpan().SequenceEqual(body)) throw new InvalidDataException("Non-canonical journal record.");
        return value;
    }

    private static byte[] Encode<T>(T value)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value, PatchIdentity.Json);
        if (body.Length > PatchIdentity.MaxRecordBytes) throw new InvalidDataException("Journal record exceeds its bound.");
        return body;
    }

    private static string Hash(byte[] body) => Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
    private static bool FixedHash(byte[] body, string expected) =>
        expected.Length == 64 && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(body)), Encoding.ASCII.GetBytes(expected));

    private static PatchApprovedIntent ApprovedIntent(AuthenticatedAuthorityContext context, PatchAdmissionRequest admission)
    {
        if (!PatchIdentity.Valid(context, admission)) throw new ArgumentException("Exact patch admission required.");
        var proposal = admission.Plan.Nodes[0].Proposals[0];
        return new(admission.Document.Identity, admission.Document.Nodes[0].Identity, admission.Plan.Identity,
            admission.Invocation, admission.ResourceRequestIdentity, admission.Plan.Workspace,
            new WorkspacePath(proposal.RelativePath), admission.WriterProfile, proposal.OriginalVersion,
            new ResourceVersion(PatchIdentity.VersionPrefix + proposal.ProposedSha256),
            proposal.OriginalByteLength, proposal.ProposedByteLength);
    }

    private static PatchApprovedIntent IntentOf(PatchOutcomeRecord record) => new(record.DocumentIdentity,
        record.NodeIdentity, record.PlanIdentity, record.Start.Invocation, record.Start.RequestIdentity,
        record.Start.Workspace, record.Start.Path, record.Start.ProviderProfile, record.Start.OriginalVersion,
        record.Start.ProposedVersion, record.Start.OriginalByteLength, record.Start.ProposedByteLength);

    private static bool ValidIntent(PatchApprovedIntent? intent, string operationId, AuthenticatedAuthorityContext context) =>
        intent is not null && PatchIdentity.AnyHash(intent.DocumentIdentity) && PatchIdentity.AnyHash(intent.NodeIdentity) &&
        PatchIdentity.AnyHash(intent.PlanIdentity) && intent.Invocation is not null &&
        AuthorityValidation.ValidToken(intent.Invocation.InvocationId) && intent.Invocation.InvocationId == operationId &&
        intent.Invocation.SubjectId == context.SubjectId && AuthorityValidation.ValidToken(intent.Invocation.EffectId) &&
        AuthorityValidation.ValidToken(intent.Invocation.AttemptId) && intent.Invocation.EffectScopeId == intent.PlanIdentity &&
        intent.Invocation.ParentEffectScopeId == intent.DocumentIdentity && intent.Invocation.RequestIdentity == intent.RequestIdentity &&
        AuthorityValidation.ValidToken(intent.RequestIdentity.Value) && AuthorityValidation.ValidToken(intent.Workspace.Value) &&
        intent.Path.Value == PatchIdentity.Canonical(intent.Path.Value) && intent.ProviderProfile == PatchIdentity.WriterProfile &&
        IsVersion(intent.OriginalVersion) && IsVersion(intent.ProposedVersion) &&
        intent.OriginalByteLength is >= 0 and <= 1_048_576 && intent.ProposedByteLength is >= 0 and <= 1_048_576;

    private static bool ValidApprovalRecord(PatchApprovalRecord? record) => record is not null &&
        AuthorityStoreValidation.ValidActor(record.Actor) && AuthorityValidation.ValidContext(record.Context) &&
        record.Actor.TenantId == record.Context.TenantId && AuthorityValidation.ValidToken(record.CommandId) &&
        AuthorityValidation.ValidToken(record.OperationId) && PatchIdentity.IsHash(record.AdmissionIdentity) &&
        record.ExpiresAt.Offset == TimeSpan.Zero && ValidIntent(record.Intent, record.OperationId, record.Context) &&
        (record.Revoked ? AuthorityValidation.ValidToken(record.RevokeCommandId) && record.RevokedBy is not null &&
            AuthorityStoreValidation.ValidActor(record.RevokedBy) && record.RevokedBy.TenantId == record.Actor.TenantId :
            record.RevokeCommandId is null && record.RevokedBy is null);

    private static bool IsVersion(ResourceVersion version) =>
        version.Value.StartsWith(PatchIdentity.VersionPrefix, StringComparison.Ordinal) && PatchIdentity.AnyHash(version.Value[PatchIdentity.VersionPrefix.Length..]);

    private static async ValueTask InsertApprovalAsync(SqliteConnection connection, SqliteTransaction transaction,
        PatchApprovalRecord record, CancellationToken ct)
    {
        var body = Encode(record);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO main.hp_approvals(tenant_id,operation_id,body,body_hash,reserved_bytes) VALUES($tenant,$operation,$body,$hash,$reserved)";
        command.Parameters.AddWithValue("$tenant", record.Actor.TenantId);
        command.Parameters.AddWithValue("$operation", record.OperationId);
        command.Parameters.AddWithValue("$body", body);
        command.Parameters.AddWithValue("$hash", Hash(body));
        command.Parameters.AddWithValue("$reserved", PatchIdentity.MaxRecordBytes);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async ValueTask InsertOutcomeAsync(SqliteConnection connection, SqliteTransaction transaction,
        PatchOutcomeRecord record, CancellationToken ct)
    {
        var body = Encode(record);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO main.hp_outcomes(tenant_id,operation_id,body,body_hash,reserved_bytes) VALUES($tenant,$operation,$body,$hash,$reserved)";
        command.Parameters.AddWithValue("$tenant", record.Actor.TenantId);
        command.Parameters.AddWithValue("$operation", record.OperationId);
        command.Parameters.AddWithValue("$body", body);
        command.Parameters.AddWithValue("$hash", Hash(body));
        command.Parameters.AddWithValue("$reserved", PatchIdentity.MaxRecordBytes);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async ValueTask UpdateApprovalAsync(SqliteConnection connection, SqliteTransaction transaction,
        PatchApprovalRecord expected, PatchApprovalRecord updated, CancellationToken ct)
    {
        if (!ValidApprovalRecord(updated)) throw new InvalidDataException();
        var oldBody = Encode(expected);
        var newBody = Encode(updated);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE main.hp_approvals SET body=$body,body_hash=$hash WHERE tenant_id=$tenant AND operation_id=$operation AND body_hash=$oldHash";
        command.Parameters.AddWithValue("$body", newBody);
        command.Parameters.AddWithValue("$hash", Hash(newBody));
        command.Parameters.AddWithValue("$tenant", expected.Actor.TenantId);
        command.Parameters.AddWithValue("$operation", expected.OperationId);
        command.Parameters.AddWithValue("$oldHash", Hash(oldBody));
        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) throw new InvalidDataException("Approval changed concurrently.");
    }

    private static async ValueTask UpdateOutcomeAsync(SqliteConnection connection, SqliteTransaction transaction,
        PatchOutcomeRecord expected, PatchOutcomeRecord updated, CancellationToken ct)
    {
        if (!PatchIdentity.ValidRecord(updated)) throw new InvalidDataException();
        var oldBody = Encode(expected);
        var newBody = Encode(updated);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE main.hp_outcomes SET body=$body,body_hash=$hash WHERE tenant_id=$tenant AND operation_id=$operation AND body_hash=$oldHash";
        command.Parameters.AddWithValue("$body", newBody);
        command.Parameters.AddWithValue("$hash", Hash(newBody));
        command.Parameters.AddWithValue("$tenant", expected.Actor.TenantId);
        command.Parameters.AddWithValue("$operation", expected.OperationId);
        command.Parameters.AddWithValue("$oldHash", Hash(oldBody));
        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) throw new InvalidDataException("Outcome changed concurrently.");
    }

    private static async ValueTask<bool> ExistsAsync(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    private static async ValueTask<object?> ScalarAsync(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }
}
