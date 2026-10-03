using System.Globalization;
using Microsoft.Data.Sqlite;
using Penghou.Hufu.Sqlite;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Hufu.Zhinu.Sqlite;

/// <summary>
/// Adapts the trusted Zhinu database owner for Hufu's co-located transaction.
/// The owner must refer to the one physical database file used by Zhinu and Hufu.
/// </summary>
public sealed class ZhinuSqliteAuthorityDatabase : ISqliteAuthorityDatabase
{
    private readonly IZhinuSqliteDatabase _database;
    private readonly string _databasePath;

    public ZhinuSqliteAuthorityDatabase(IZhinuSqliteDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        var options = database.Options;
        if (!options.EnableWal || options.Pooling || options.BusyTimeout <= TimeSpan.Zero ||
            options.BusyTimeout > TimeSpan.FromSeconds(30) || string.IsNullOrWhiteSpace(options.DatabasePath) ||
            options.DatabasePath == ":memory:" || options.DatabasePath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The start profile requires a disk-backed WAL database with pooling disabled and a bounded busy timeout.", nameof(database));
        _databasePath = Path.GetFullPath(options.DatabasePath);
    }

    public TimeProvider TimeProvider => _database.TimeProvider;

    public async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        await _database.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var actualPath = Path.GetFullPath(connection.DataSource);
            if (!string.Equals(actualPath, _databasePath, StringComparison.Ordinal))
                throw new InvalidDataException("The database owner returned a connection to a different file.");

            if (await ScalarAsync(connection, "SELECT version FROM main.zhinu_schema WHERE id=1", cancellationToken).ConfigureAwait(false)
                is not long version || version != 5)
                throw new InvalidDataException("The Zhinu schema version is unsupported.");
            if (!string.Equals(Convert.ToString(await ScalarAsync(connection, "PRAGMA journal_mode", cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture),
                    "wal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("WAL mode is required.");
            if (Convert.ToInt64(await ScalarAsync(connection, "PRAGMA synchronous", cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 2 ||
                Convert.ToInt64(await ScalarAsync(connection, "PRAGMA foreign_keys", cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 1)
                throw new InvalidDataException("Full synchronous durability and foreign-key enforcement are required.");
            var busyTimeout = Convert.ToInt64(await ScalarAsync(connection, "PRAGMA busy_timeout", cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            var expectedTimeout = (long)_database.Options.BusyTimeout.TotalMilliseconds;
            if (busyTimeout <= 0 || busyTimeout > 30_000 || busyTimeout != expectedTimeout)
                throw new InvalidDataException("The SQLite busy timeout is outside the configured bound.");
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }
}
