using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DotNetQuery.Persistence;

/// <summary>
/// A SQLite-backed <see cref="IQueryPersister"/> that stores each query as a row in a single table.
/// Suitable for durable cross-session caching on desktop and mobile apps.
/// </summary>
/// <remarks>
/// The persister holds one open connection for its lifetime (required so an in-memory database
/// survives between calls) and serializes access with a semaphore, since a single
/// <see cref="SqliteConnection"/> does not support concurrent commands. Dispose it to close the
/// connection.
/// </remarks>
public sealed class SqliteQueryPersister : IQueryPersister, IDisposable, IAsyncDisposable
{
    private const string TableName = "dotnetquery_cache";

    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;

    /// <summary>
    /// Creates a persister over the database identified by <paramref name="connectionString"/>
    /// (for example <c>"Data Source=cache.db"</c> or <c>"Data Source=:memory:"</c>), opening the
    /// connection and creating the backing table if it does not exist.
    /// </summary>
    public SqliteQueryPersister(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connection = new SqliteConnection(connectionString);
        _connection.Open();
        EnsureSchema();
    }

    /// <summary>
    /// Creates a file-backed persister at <paramref name="path"/>. Convenience over building a
    /// <c>Data Source=</c> connection string by hand.
    /// </summary>
    public static SqliteQueryPersister ForFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new SqliteQueryPersister($"Data Source={path}");
    }

    private void EnsureSchema()
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            $"CREATE TABLE IF NOT EXISTS {TableName} (" +
            "key TEXT PRIMARY KEY, " +
            "payload TEXT NOT NULL, " +
            "updated_at TEXT NOT NULL)";
        command.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public async Task SaveAsync(PersistedQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                $"INSERT INTO {TableName} (key, payload, updated_at) VALUES ($key, $payload, $updated) " +
                "ON CONFLICT(key) DO UPDATE SET payload = excluded.payload, updated_at = excluded.updated_at";
            command.Parameters.AddWithValue("$key", query.Key);
            command.Parameters.AddWithValue("$payload", query.Payload);
            command.Parameters.AddWithValue("$updated", query.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<PersistedQuery?> LoadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"SELECT key, payload, updated_at FROM {TableName} WHERE key = $key";
            command.Parameters.AddWithValue("$key", key);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PersistedQuery>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"SELECT key, payload, updated_at FROM {TableName}";

            var results = new List<PersistedQuery>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(Read(reader));
            }

            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"DELETE FROM {TableName} WHERE key = $key";
            command.Parameters.AddWithValue("$key", key);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"DELETE FROM {TableName}";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static PersistedQuery Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Clear the pool first so the physical database file handle is released immediately, letting
        // callers delete or replace the file right after disposal.
        SqliteConnection.ClearPool(_connection);
        _connection.Dispose();
        _gate.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        SqliteConnection.ClearPool(_connection);
        await _connection.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
