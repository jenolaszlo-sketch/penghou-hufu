using Microsoft.Data.Sqlite;

namespace Penghou.Hufu.Biscuit.Sqlite;

public sealed partial class SqliteBiscuitCredentialRegistry
{
    private async ValueTask<BiscuitRegistryStatus> CheckActiveAsync(SqliteConnection connection, SqliteTransaction transaction,
        BiscuitCredentialRegistration credential, DateTimeOffset now, CancellationToken ct)
    {
        if (credential.EffectiveGrant.NotBefore > now || credential.EffectiveGrant.ExpiresAt <= now) return BiscuitRegistryStatus.Conflict;
        var leaf = credential;
        for (var depth = 0; depth < BiscuitProfile.MaximumBlocks; depth++)
        {
            var meaning = await ScalarAsync(connection, transaction,
                "SELECT meaning FROM main.hufu_biscuit_versions WHERE realm=$realm AND tenant=$tenant AND layer=$layer AND grant_id=$grant AND version=$version", ct,
                ("$realm", credential.Binding.Realm), ("$tenant", credential.Binding.Context.TenantId),
                ("$layer", credential.LayerId), ("$grant", credential.RootGrant.Id), ("$version", credential.GrantVersion));
            if (!Equals(meaning, credential.GrantVersionIdentity)) throw new InvalidDataException();
            if (credential.ParentFingerprint is not string parentId)
            {
                if (credential.BlockCount != 1) throw new InvalidDataException();
                return await CheckRevocationAsync(connection, transaction, leaf, ct);
            }
            var parent = await LoadRegistrationAsync(connection, transaction, credential.Binding.Realm,
                credential.Binding.Context.TenantId, parentId, ct);
            if (parent is null || !ValidChild(parent, credential)) throw new InvalidDataException();
            credential = parent;
        }
        throw new InvalidDataException("Credential ancestry exceeds its bound.");
    }

    private static async ValueTask<BiscuitRegistryStatus> CheckRevocationAsync(SqliteConnection connection, SqliteTransaction transaction,
        BiscuitCredentialRegistration credential, CancellationToken ct)
    {
        if (await ScalarAsync(connection, transaction,
            "SELECT 1 FROM main.hufu_biscuit_retired_keys WHERE realm=$realm AND id=$id", ct,
            ("$realm", credential.Binding.Realm), ("$id", credential.RootKeyId)) is not null) return BiscuitRegistryStatus.Revoked;
        foreach (var id in credential.RevocationIds)
            if (await ScalarAsync(connection, transaction,
                "SELECT 1 FROM main.hufu_biscuit_revocations WHERE realm=$realm AND tenant=$tenant AND id=$id", ct,
                ("$realm", credential.Binding.Realm), ("$tenant", credential.Binding.Context.TenantId), ("$id", id)) is not null)
                return BiscuitRegistryStatus.Revoked;
        return BiscuitRegistryStatus.Active;
    }

    private static async ValueTask<BiscuitCredentialRegistration?> LoadRegistrationAsync(SqliteConnection connection, SqliteTransaction transaction,
        string realm, string tenant, string fingerprint, CancellationToken ct)
    {
        var bytes = await LoadBodyAsync(connection, transaction,
            "SELECT CASE WHEN typeof(body)='blob' AND length(body)<=$max THEN body END,hash FROM main.hufu_biscuit_credentials WHERE realm=$realm AND tenant=$tenant AND fingerprint=$fingerprint",
            ct, ("$realm", realm), ("$tenant", tenant), ("$fingerprint", fingerprint));
        if (bytes is null) return null;
        var value = BiscuitCodec.DecodeRegistration(bytes);
        if (value.Fingerprint != fingerprint || value.Binding.Context.TenantId != tenant || value.Binding.Realm != realm) throw new InvalidDataException();
        return value;
    }

    private static async ValueTask<BiscuitDecisionEvidence?> LoadEvidenceAsync(SqliteConnection connection, SqliteTransaction transaction,
        string tenant, string id, CancellationToken ct)
    {
        var bytes = await LoadBodyAsync(connection, transaction,
            "SELECT CASE WHEN typeof(body)='blob' AND length(body)<=$max THEN body END,hash FROM main.hufu_biscuit_verifications WHERE tenant=$tenant AND id=$id",
            ct, ("$tenant", tenant), ("$id", id));
        if (bytes is null) return null;
        var value = BiscuitCodec.DecodeEvidence(bytes);
        if (!ValidEvidence(value) || value.Actor.TenantId != tenant || value.Id != id) throw new InvalidDataException();
        return value;
    }

    private static async ValueTask<byte[]?> LoadBodyAsync(SqliteConnection connection, SqliteTransaction transaction,
        string sql, CancellationToken ct, params (string Name, object Value)[] values)
    {
        using var command = Command(connection, transaction, sql, values);
        command.Parameters.AddWithValue("$max", MaxBodyBytes);
        using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        if (reader.IsDBNull(0)) throw new InvalidDataException();
        var bytes = (byte[])reader.GetValue(0);
        if (bytes.Length is < 1 or > MaxBodyBytes || BiscuitProfile.Hash(bytes) != reader.GetString(1)) throw new InvalidDataException();
        return bytes;
    }

    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = await _owner.OpenAsync(ct);
        try { await ValidateDatabaseAsync(connection, ct); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private async ValueTask ValidateDatabaseAsync(SqliteConnection connection, CancellationToken ct)
    {
        bool found = false;
        using (var list = connection.CreateCommand())
        {
            list.CommandText = "PRAGMA database_list";
            using var reader = await list.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var name = reader.GetString(1);
                if (name == "temp") continue; // Queries explicitly address main.
                if (name != "main" || !Path.GetFullPath(reader.GetString(2)).Equals(_path,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new InvalidDataException();
                found = true;
            }
        }
        if (!found) throw new InvalidDataException();
        foreach (var (pragma, expected) in new[] { ("journal_mode", "wal"), ("synchronous", "2"), ("foreign_keys", "1") })
        {
            using var sql = connection.CreateCommand(); sql.CommandText = "PRAGMA " + pragma;
            if (!string.Equals(Convert.ToString(await sql.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture),
                expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
        }
    }

    private static async ValueTask EnsureSchemaAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        if (await ScalarAsync(connection, transaction,
            "SELECT 1 FROM main.sqlite_schema WHERE name='hufu_biscuit_meta' AND type='table'", ct) is null)
        {
            if (await ScalarAsync(connection, transaction,
                "SELECT 1 FROM main.sqlite_schema WHERE name GLOB 'hufu_biscuit_*' LIMIT 1", ct) is not null) throw new InvalidDataException();
            await ExecuteAsync(connection, transaction, """
                CREATE TABLE main.hufu_biscuit_meta(id INTEGER PRIMARY KEY CHECK(id=1),codec TEXT NOT NULL);
                INSERT INTO main.hufu_biscuit_meta VALUES(1,'hufu-biscuit-json-v1');
                CREATE TABLE main.hufu_biscuit_credentials(realm TEXT NOT NULL,tenant TEXT NOT NULL,fingerprint TEXT NOT NULL,body BLOB NOT NULL,hash TEXT NOT NULL,actor TEXT NOT NULL,session TEXT NOT NULL,PRIMARY KEY(realm,tenant,fingerprint));
                CREATE TABLE main.hufu_biscuit_versions(realm TEXT NOT NULL,tenant TEXT NOT NULL,layer TEXT NOT NULL,grant_id TEXT NOT NULL,version TEXT NOT NULL,meaning TEXT NOT NULL,PRIMARY KEY(realm,tenant,layer,grant_id,version));
                CREATE TABLE main.hufu_biscuit_verifications(tenant TEXT NOT NULL,id TEXT NOT NULL,body BLOB NOT NULL,hash TEXT NOT NULL,PRIMARY KEY(tenant,id));
                CREATE TABLE main.hufu_biscuit_revocations(realm TEXT NOT NULL,tenant TEXT NOT NULL,id TEXT NOT NULL,actor TEXT NOT NULL,session TEXT NOT NULL,reason TEXT NOT NULL,at INTEGER NOT NULL,PRIMARY KEY(realm,tenant,id));
                CREATE TABLE main.hufu_biscuit_retired_keys(realm TEXT NOT NULL,id TEXT NOT NULL,tenant TEXT NOT NULL,actor TEXT NOT NULL,session TEXT NOT NULL,reason TEXT NOT NULL,at INTEGER NOT NULL,PRIMARY KEY(realm,id));
                """, ct);
        }
        if (!Equals(await ScalarAsync(connection, transaction, "SELECT codec FROM main.hufu_biscuit_meta WHERE id=1", ct), "hufu-biscuit-json-v1"))
            throw new InvalidDataException();
    }

    private async ValueTask<bool> HasCapacityAsync(SqliteConnection connection, SqliteTransaction transaction,
        int additionalBytes, int additionalEntries, CancellationToken ct)
    {
        // Persistent metadata bounds; these do not assert a managed/native memory cap.
        using var command = Command(connection, transaction, """
            SELECT
            (SELECT count(*) FROM main.hufu_biscuit_credentials)+(SELECT count(*) FROM main.hufu_biscuit_versions)+
            (SELECT count(*) FROM main.hufu_biscuit_verifications)+(SELECT count(*) FROM main.hufu_biscuit_revocations)+
            (SELECT count(*) FROM main.hufu_biscuit_retired_keys),
            COALESCE((SELECT sum(length(body)+length(CAST(actor||session||realm||tenant||fingerprint||hash AS BLOB))) FROM main.hufu_biscuit_credentials),0)+
            COALESCE((SELECT sum(length(body)+length(CAST(tenant||id||hash AS BLOB))) FROM main.hufu_biscuit_verifications),0)+
            COALESCE((SELECT sum(length(CAST(realm||tenant||layer||grant_id||version||meaning AS BLOB))) FROM main.hufu_biscuit_versions),0)+
            COALESCE((SELECT sum(length(CAST(realm||tenant||id||actor||session||reason AS BLOB))+8) FROM main.hufu_biscuit_revocations),0)+
            COALESCE((SELECT sum(length(CAST(realm||id||tenant||actor||session||reason AS BLOB))+8) FROM main.hufu_biscuit_retired_keys),0)
            """);
        using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidDataException();
        var entries = reader.GetInt64(0); var bytes = reader.GetInt64(1);
        return entries >= 0 && bytes >= 0 && entries + additionalEntries <= _options.MaxEntries && bytes + additionalBytes <= _options.MaxStoredBytes;
    }

    private static int TextBytes(params string[] values) => values.Sum(BiscuitProfile.Utf8.GetByteCount);
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction,
        string sql, params (string Name, object Value)[] values)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
        return command;
    }
    private static async ValueTask<object?> ScalarAsync(SqliteConnection connection, SqliteTransaction transaction,
        string sql, CancellationToken ct, params (string Name, object Value)[] values)
    {
        using var command = Command(connection, transaction, sql, values);
        var value = await command.ExecuteScalarAsync(ct); return value is DBNull ? null : value;
    }
    private static async ValueTask<int> ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction,
        string sql, CancellationToken ct, params (string Name, object Value)[] values)
    {
        using var command = Command(connection, transaction, sql, values); return await command.ExecuteNonQueryAsync(ct);
    }
}
