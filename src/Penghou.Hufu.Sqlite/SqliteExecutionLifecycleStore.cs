using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Penghou.Hufu.Sqlite;

/// <summary>
/// Durable, ordered, idempotent execution-lifecycle authority state for
/// <see cref="IExecutionLifecycleStore"/>. Only neutral transitions are stored;
/// workflow events live in the adapter. Apply is atomic: the state pointer, the
/// sequence compare-and-set and the transition receipt become visible together
/// or not at all. Reopen recovers the exact state and receipts.
/// </summary>
public sealed class SqliteExecutionLifecycleStore : IExecutionLifecycleStore
{
    private const long ApplicationId = 0x4855464C; // "HUFL"
    private const int SchemaVersion = 1;
    private const string Codec = "hufu-lifecycle-v1";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly string _connectionString;
    private readonly TimeProvider _clock;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026", Justification = "Distinct preview constructor surface; a string path and a trusted database owner have different first-parameter types.")]
    public SqliteExecutionLifecycleStore(string databasePath, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (databasePath == ":memory:" || databasePath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A dedicated disk database path is required.", nameof(databasePath));
        _clock = timeProvider ?? TimeProvider.System;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 1
        }.ToString();
    }

    public async ValueTask<ExecutionLifecycleApplyResult> ApplyAsync(
        ExecutionLifecycleTransition transition,
        CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        ct.ThrowIfCancellationRequested();
        if (transition is null) return new(ExecutionLifecycleApplyStatus.InvalidRequest);
        var key = BindingKey(transition.Binding);
        try
        {
            using var connection = await OpenAsync(ct).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            var byTransition = await LoadByTransitionAsync(connection, transaction, transition.TransitionId, ct).ConfigureAwait(false);
            if (byTransition is { } existing)
            {
                if (existing.Key == key && existing.Intent == IntentOf(transition))
                    return new(ExecutionLifecycleApplyStatus.Replayed, existing.ToReceipt());
                return new(ExecutionLifecycleApplyStatus.Conflict);
            }
            var current = await LoadByBindingAsync(connection, transaction, key, ct).ConfigureAwait(false);
            var currentSequence = current?.Sequence ?? 0;
            if (transition.ExpectedSequence != currentSequence)
                return new(ExecutionLifecycleApplyStatus.Stale, current?.ToReceipt());
            var (state, status) = NextState(current?.State, transition);
            if (status != ExecutionLifecycleApplyStatus.Applied)
                return new(status, current?.ToReceipt());
            var sequence = currentSequence + 1;
            var recordedAt = _clock.GetUtcNow();
            if (current is null)
            {
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO hufu_lifecycle(binding_key,tenant,subject,run,sequence,state,generation,fence,transition_id,kind,reason,recorded_at) " +
                    "VALUES($key,$tenant,$subject,$run,$seq,$state,$generation,$fence,$transition,$kind,$reason,$recorded)", ct,
                    ("$key", key), ("$tenant", transition.Binding.TenantId), ("$subject", transition.Binding.SubjectId),
                    ("$run", transition.Binding.RunId), ("$seq", sequence), ("$state", (int)state),
                    ("$generation", transition.Generation), ("$fence", transition.Fence),
                    ("$transition", transition.TransitionId), ("$kind", (int)transition.Kind),
                    ("$reason", (int)transition.Reason), ("$recorded", recordedAt.ToString("O"))).ConfigureAwait(false);
            }
            else if (await ExecuteAsync(connection, transaction,
                "UPDATE hufu_lifecycle SET sequence=$seq,state=$state,generation=$generation,fence=$fence,transition_id=$transition,kind=$kind,reason=$reason,recorded_at=$recorded " +
                "WHERE binding_key=$key AND sequence=$expected", ct,
                ("$seq", sequence), ("$state", (int)state), ("$generation", transition.Generation), ("$fence", transition.Fence),
                ("$transition", transition.TransitionId), ("$kind", (int)transition.Kind), ("$reason", (int)transition.Reason),
                ("$recorded", recordedAt.ToString("O")), ("$key", key), ("$expected", currentSequence)).ConfigureAwait(false) != 1)
                throw new InvalidDataException();
            ct.ThrowIfCancellationRequested();
            transaction.Commit();
            var receipt = new ExecutionLifecycleReceipt(transition.Binding, sequence, state, transition.Kind,
                transition.Reason, transition.TransitionId, transition.Generation, transition.Fence, recordedAt);
            return new(ExecutionLifecycleApplyStatus.Applied, receipt);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return new(ExecutionLifecycleApplyStatus.Unavailable); }
    }

    /// <summary>Returns current state or null when no transition exists. Throws on store failure so callers can fail closed.</summary>
    public async ValueTask<ExecutionLifecycleState?> GetStateAsync(
        ExecutionLifecycleBinding binding,
        CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(binding);
        var key = BindingKey(binding);
        using var connection = await OpenAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var state = await ScalarAsync(connection, transaction,
            "SELECT state FROM hufu_lifecycle WHERE binding_key=$key", ct, ("$key", key)).ConfigureAwait(false);
        return state is null ? null : (ExecutionLifecycleState)Convert.ToInt32(state);
    }

    private static (ExecutionLifecycleState State, ExecutionLifecycleApplyStatus Status) NextState(
        ExecutionLifecycleState? current, ExecutionLifecycleTransition transition)
    {
        if (current == ExecutionLifecycleState.Terminal)
            return (ExecutionLifecycleState.Terminal, ExecutionLifecycleApplyStatus.Terminated);
        switch (transition.Kind)
        {
            case ExecutionLifecycleTransitionKind.Suspend:
                if (current == ExecutionLifecycleState.Suspended)
                    return (ExecutionLifecycleState.Suspended, ExecutionLifecycleApplyStatus.Conflict);
                return (ExecutionLifecycleState.Suspended, ExecutionLifecycleApplyStatus.Applied);
            case ExecutionLifecycleTransitionKind.Resume:
                if (current != ExecutionLifecycleState.Suspended)
                    return (current ?? ExecutionLifecycleState.Active, ExecutionLifecycleApplyStatus.Conflict);
                return (ExecutionLifecycleState.Active, ExecutionLifecycleApplyStatus.Applied);
            case ExecutionLifecycleTransitionKind.Terminate:
                return (ExecutionLifecycleState.Terminal, ExecutionLifecycleApplyStatus.Applied);
            default:
                return (current ?? ExecutionLifecycleState.Active, ExecutionLifecycleApplyStatus.InvalidRequest);
        }
    }

    private static (int Kind, int Reason, string Generation, string Fence) IntentOf(ExecutionLifecycleTransition transition) =>
        ((int)transition.Kind, (int)transition.Reason, transition.Generation, transition.Fence);

    private static async Task<Row?> LoadByBindingAsync(SqliteConnection connection, SqliteTransaction transaction,
        string key, CancellationToken ct)
    {
        using var command = Command(connection, transaction,
            "SELECT tenant,subject,run,sequence,state,generation,fence,transition_id,kind,reason,recorded_at FROM hufu_lifecycle WHERE binding_key=$key",
            ("$key", key));
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadRow(reader, key) : null;
    }

    private static async Task<Row?> LoadByTransitionAsync(SqliteConnection connection, SqliteTransaction transaction,
        string transitionId, CancellationToken ct)
    {
        using var command = Command(connection, transaction,
            "SELECT binding_key,tenant,subject,run,sequence,state,generation,fence,transition_id,kind,reason,recorded_at FROM hufu_lifecycle WHERE transition_id=$transition",
            ("$transition", transitionId));
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        var key = reader.GetString(0);
        return ReadRow(reader, key, offset: 1);
    }

    private static Row ReadRow(SqliteDataReader reader, string key, int offset = 0)
    {
        var binding = new ExecutionLifecycleBinding(reader.GetString(offset), reader.GetString(offset + 1), reader.GetString(offset + 2));
        var sequence = reader.GetInt64(offset + 3);
        var state = (ExecutionLifecycleState)reader.GetInt32(offset + 4);
        var kind = (ExecutionLifecycleTransitionKind)reader.GetInt32(offset + 8);
        var reason = (ExecutionLifecycleTerminalReason)reader.GetInt32(offset + 9);
        var recordedAt = DateTimeOffset.Parse(reader.GetString(offset + 10), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind);
        if (sequence < 1 || !Enum.IsDefined(state) || !Enum.IsDefined(kind) || !Enum.IsDefined(reason))
            throw new InvalidDataException();
        return new Row(key, binding, sequence, state, kind, reason,
            reader.GetString(offset + 5), reader.GetString(offset + 6), reader.GetString(offset + 7), recordedAt);
    }

    private sealed record Row(string Key, ExecutionLifecycleBinding Binding, long Sequence, ExecutionLifecycleState State,
        ExecutionLifecycleTransitionKind Kind, ExecutionLifecycleTerminalReason Reason,
        string Generation, string Fence, string TransitionId, DateTimeOffset RecordedAt)
    {
        internal (int Kind, int Reason, string Generation, string Fence) Intent => ((int)Kind, (int)Reason, Generation, Fence);
        internal ExecutionLifecycleReceipt ToReceipt() =>
            new(Binding, Sequence, State, Kind, Reason, TransitionId, Generation, Fence, RecordedAt);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            connection.DefaultTimeout = 1;
            await ExecuteAsync(connection, null,
                "PRAGMA foreign_keys=ON; PRAGMA trusted_schema=OFF; PRAGMA busy_timeout=1000; PRAGMA synchronous=FULL", ct).ConfigureAwait(false);
            using (var transaction = connection.BeginTransaction(deferred: false))
            {
                var id = Convert.ToInt64(await ScalarAsync(connection, transaction, "PRAGMA application_id", ct).ConfigureAwait(false));
                var version = Convert.ToInt64(await ScalarAsync(connection, transaction, "PRAGMA user_version", ct).ConfigureAwait(false));
                var hasTable = await ExistsAsync(connection, transaction,
                    "SELECT 1 FROM sqlite_schema WHERE type='table' AND name='hufu_lifecycle_meta'", ct).ConfigureAwait(false);
                if (id == 0 && version == 0 && !hasTable && !await ExistsAsync(connection, transaction,
                    "SELECT 1 FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%'", ct).ConfigureAwait(false))
                {
                    await ExecuteAsync(connection, transaction, Schema, ct).ConfigureAwait(false);
                }
                else if (id != ApplicationId || version != SchemaVersion) throw new InvalidDataException();
                var codec = await ScalarAsync(connection, transaction, "SELECT codec FROM hufu_lifecycle_meta WHERE id=1", ct).ConfigureAwait(false);
                if (!Equals(codec, Codec)) throw new InvalidDataException();
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

    private static string BindingKey(ExecutionLifecycleBinding binding)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, StrictUtf8, true);
        Write(writer, "Penghou.Hufu.ExecutionLifecycle.Binding.v1");
        Write(writer, binding.TenantId); Write(writer, binding.SubjectId); Write(writer, binding.RunId);
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length))).ToLowerInvariant();

        static void Write(BinaryWriter writer, string value)
        {
            var bytes = StrictUtf8.GetBytes(value);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object Value)[] values)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value);
        return command;
    }

    private static async Task<int> ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        CancellationToken ct, params (string Name, object Value)[] values)
    {
        ct.ThrowIfCancellationRequested();
        using var command = Command(connection, transaction, sql, values);
        var result = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return result;
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        CancellationToken ct, params (string Name, object Value)[] values)
    {
        ct.ThrowIfCancellationRequested();
        using var command = Command(connection, transaction, sql, values);
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return result is null or DBNull ? null : result;
    }

    private static async Task<bool> ExistsAsync(SqliteConnection connection, SqliteTransaction transaction, string sql,
        CancellationToken ct, params (string Name, object Value)[] values) =>
        await ScalarAsync(connection, transaction, sql, ct, values).ConfigureAwait(false) is not null;

    private const string Schema = """
        CREATE TABLE hufu_lifecycle_meta(id INTEGER PRIMARY KEY CHECK(id=1),codec TEXT NOT NULL);
        INSERT INTO hufu_lifecycle_meta VALUES(1,'hufu-lifecycle-v1');
        CREATE TABLE hufu_lifecycle(
            binding_key TEXT PRIMARY KEY,
            tenant TEXT NOT NULL,subject TEXT NOT NULL,run TEXT NOT NULL,
            sequence INTEGER NOT NULL CHECK(sequence>0),
            state INTEGER NOT NULL CHECK(state BETWEEN 0 AND 2),
            generation TEXT NOT NULL,fence TEXT NOT NULL,
            transition_id TEXT NOT NULL,
            kind INTEGER NOT NULL CHECK(kind BETWEEN 0 AND 2),
            reason INTEGER NOT NULL CHECK(reason BETWEEN 0 AND 4),
            recorded_at TEXT NOT NULL);
        CREATE UNIQUE INDEX hufu_lifecycle_transition ON hufu_lifecycle(transition_id);
        PRAGMA application_id=1213548108;
        PRAGMA user_version=1;
        """;
}
