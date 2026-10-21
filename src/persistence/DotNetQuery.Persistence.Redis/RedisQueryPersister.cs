using System.Text.Json;
using StackExchange.Redis;

namespace DotNetQuery.Persistence;

/// <summary>
/// A Redis-backed <see cref="IQueryPersister"/> that stores every persisted query as a field in a single
/// Redis hash. Suitable for shared or server-side caches where multiple instances read the same data.
/// </summary>
/// <remarks>
/// The <see cref="ConnectionMultiplexer"/> is thread-safe and multiplexes all operations, so no external
/// locking is required. When the persister creates its own connection it disposes it; when given a shared
/// connection it leaves it open.
/// </remarks>
public sealed class RedisQueryPersister : IQueryPersister, IDisposable, IAsyncDisposable
{
    /// <summary>The default Redis hash key under which persisted queries are stored.</summary>
    public const string DefaultHashKey = "dotnetquery:queries";

    private readonly IConnectionMultiplexer _connection;
    private readonly bool _ownsConnection;
    private readonly RedisKey _hashKey;
    private readonly JsonSerializerOptions? _jsonOptions;
    private int _disposed;

    /// <summary>
    /// Creates a persister that connects to Redis using <paramref name="configuration"/> (for example
    /// <c>"localhost:6379"</c>) and owns the resulting connection.
    /// </summary>
    /// <param name="configuration">A StackExchange.Redis configuration string.</param>
    /// <param name="hashKey">The Redis hash key to store queries under. Defaults to <see cref="DefaultHashKey"/>.</param>
    /// <param name="jsonOptions">Serialization options for the stored envelope.</param>
    public RedisQueryPersister(string configuration, string? hashKey = null, JsonSerializerOptions? jsonOptions = null)
        : this(ConnectionMultiplexer.Connect(configuration), hashKey, jsonOptions, ownsConnection: true)
    {
    }

    /// <summary>
    /// Creates a persister over an existing (shared) <paramref name="connection"/>, which is left open
    /// when this persister is disposed.
    /// </summary>
    /// <param name="connection">A live StackExchange.Redis connection.</param>
    /// <param name="hashKey">The Redis hash key to store queries under. Defaults to <see cref="DefaultHashKey"/>.</param>
    /// <param name="jsonOptions">Serialization options for the stored envelope.</param>
    public RedisQueryPersister(IConnectionMultiplexer connection, string? hashKey = null, JsonSerializerOptions? jsonOptions = null)
        : this(connection, hashKey, jsonOptions, ownsConnection: false)
    {
    }

    private RedisQueryPersister(IConnectionMultiplexer connection, string? hashKey, JsonSerializerOptions? jsonOptions, bool ownsConnection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
        _ownsConnection = ownsConnection;
        _hashKey = hashKey ?? DefaultHashKey;
        _jsonOptions = jsonOptions;
    }

    private IDatabase Database => _connection.GetDatabase();

    /// <inheritdoc />
    public async Task SaveAsync(PersistedQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        var payload = PersistedQueryJson.Serialize(query, _jsonOptions);
        await Database.HashSetAsync(_hashKey, query.Key, payload).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PersistedQuery?> LoadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        var value = await Database.HashGetAsync(_hashKey, key).ConfigureAwait(false);
        return value.IsNullOrEmpty ? null : PersistedQueryJson.Deserialize((string)value!, _jsonOptions);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PersistedQuery>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = await Database.HashGetAllAsync(_hashKey).ConfigureAwait(false);
        var results = new List<PersistedQuery>(entries.Length);
        foreach (var entry in entries)
        {
            if (entry.Value.IsNullOrEmpty)
            {
                continue;
            }

            try
            {
                if (PersistedQueryJson.Deserialize((string)entry.Value!, _jsonOptions) is { } query)
                {
                    results.Add(query);
                }
            }
            catch (JsonException)
            {
                // Skip corrupt or incompatible records instead of failing the whole restore.
            }
        }

        return results;
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        await Database.HashDeleteAsync(_hashKey, key).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Database.KeyDeleteAsync(_hashKey).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_ownsConnection)
        {
            _connection.Dispose();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_ownsConnection)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
