using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace DotNetQuery.Persistence;

/// <summary>Options for a <see cref="QueryPersistenceManager"/>.</summary>
public sealed record QueryPersistenceOptions
{
    /// <summary>Serialization options for query payloads. Defaults to <see cref="JsonSerializerDefaults.Web"/>.</summary>
    public JsonSerializerOptions? JsonOptions { get; init; }

    /// <summary>
    /// The maximum age of persisted data. Older entries are discarded (and deleted from the store) on
    /// restore instead of being shown. <see langword="null"/> (the default) keeps data indefinitely.
    /// </summary>
    public TimeSpan? MaxAge { get; init; }

    /// <summary>
    /// A version tag for the persisted data (for example your app or schema version). Data persisted under
    /// a different buster is ignored on restore, so a deploy that changes data shapes cannot resurrect
    /// incompatible payloads.
    /// </summary>
    public string? Buster { get; init; }

    /// <summary>The clock used for <see cref="MaxAge"/> and auto-persist scheduling. Defaults to <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider? TimeProvider { get; init; }
}

/// <summary>
/// Persists and restores query data between an <see cref="IQueryClient"/> and an
/// <see cref="IQueryPersister"/>.
/// </summary>
/// <remarks>
/// <para>
/// Register the query keys you want persisted with <see cref="Register{T}(QueryKey)"/> (or the
/// <see cref="JsonTypeInfo{T}"/> overload for trimmed/NativeAOT apps). Registration captures the type in a
/// closure, so no runtime generic instantiation is required.
/// </para>
/// <para>
/// Restored data is hydrated with its original timestamp: data that is still within its stale time is
/// served without a request, while older data is shown immediately and refetched in the background on
/// first use. Call <see cref="StartAutoPersist"/> to save registered queries whenever their data changes.
/// </para>
/// </remarks>
public sealed class QueryPersistenceManager : IDisposable
{
    private static readonly JsonSerializerOptions DefaultJson = new(JsonSerializerDefaults.Web);

    private readonly IQueryClient _client;
    private readonly IQueryPersister _persister;
    private readonly QueryPersistenceOptions _options;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<QueryKey, Registration> _registrations = new();
    private readonly object _autoGate = new();
    private readonly HashSet<QueryKey> _dirty = [];
    private Action<QueryCacheChange>? _autoHandler;
    private TimeSpan _autoDelay;
    private ITimer? _autoTimer;
    private Task _autoFlush = Task.CompletedTask;

    /// <summary>Creates a manager bound to a client and a backing persister.</summary>
    public QueryPersistenceManager(IQueryClient client, IQueryPersister persister, JsonSerializerOptions? jsonOptions = null)
        : this(client, persister, new QueryPersistenceOptions { JsonOptions = jsonOptions })
    {
    }

    /// <summary>Creates a manager bound to a client and a backing persister, with options.</summary>
    public QueryPersistenceManager(IQueryClient client, IQueryPersister persister, QueryPersistenceOptions options)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _persister = persister ?? throw new ArgumentNullException(nameof(persister));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _jsonOptions = options.JsonOptions ?? DefaultJson;
        _timeProvider = options.TimeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Raised when persisting or restoring a query fails (for example a store error or a payload that no
    /// longer deserializes). The failing entry is skipped; other queries are unaffected.
    /// </summary>
    public event Action<QueryKey, Exception>? Error;

    /// <summary>Gets the keys registered for persistence.</summary>
    public IReadOnlyCollection<QueryKey> RegisteredKeys => _registrations.Keys.ToArray();

    /// <summary>
    /// Registers a query key for persistence with data type <typeparamref name="T"/>, using reflection-based
    /// JSON serialization. In trimmed or NativeAOT apps use <see cref="Register{T}(QueryKey, JsonTypeInfo{T})"/>.
    /// </summary>
    [RequiresUnreferencedCode("JSON serialization of T may require types that cannot be statically analyzed. Use the JsonTypeInfo<T> overload.")]
    [RequiresDynamicCode("JSON serialization of T may require runtime code generation. Use the JsonTypeInfo<T> overload.")]
    public QueryPersistenceManager Register<T>(QueryKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var options = _jsonOptions;
        return Add(
            key,
            data => JsonSerializer.Serialize(data, options),
            payload => JsonSerializer.Deserialize<T>(payload, options)!);
    }

    /// <summary>
    /// Registers a query key for persistence with data type <typeparamref name="T"/>, serialized with
    /// <paramref name="typeInfo"/> (typically from a source-generated <c>JsonSerializerContext</c>). Trim- and
    /// NativeAOT-safe.
    /// </summary>
    public QueryPersistenceManager Register<T>(QueryKey key, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(typeInfo);
        return Add(
            key,
            data => JsonSerializer.Serialize(data, typeInfo),
            payload => JsonSerializer.Deserialize(payload, typeInfo)!);
    }

    /// <summary>Registers a query definition's key for persistence (reflection-based JSON).</summary>
    [RequiresUnreferencedCode("JSON serialization of T may require types that cannot be statically analyzed. Use the JsonTypeInfo<T> overload.")]
    [RequiresDynamicCode("JSON serialization of T may require runtime code generation. Use the JsonTypeInfo<T> overload.")]
    public QueryPersistenceManager Register<T>(QueryDefinition<T> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Register<T>(query.Key);
    }

    /// <summary>Registers a query definition's key for persistence, serialized with <paramref name="typeInfo"/>.</summary>
    public QueryPersistenceManager Register<T>(QueryDefinition<T> query, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Register(query.Key, typeInfo);
    }

    private QueryPersistenceManager Add<T>(QueryKey key, Func<T, string> serialize, Func<string, T> deserialize)
    {
        var registration = new Registration(
            Key: key,
            StorageKey: StorageKeyFor(key),
            Snapshot: () =>
            {
                var state = _client.GetQueryData<T>(key).State;
                return state.HasData
                    ? (serialize(state.Data!), state.DataUpdatedAt ?? _timeProvider.GetUtcNow())
                    : null;
            },
            Apply: (payload, updatedAt) => _client.HydrateQueryData(key, deserialize(payload), updatedAt),
            Gate: new SemaphoreSlim(1, 1));

        _registrations[key] = registration;
        return this;
    }

    /// <summary>Persists a single registered query's current data.</summary>
    public async Task PersistAsync(QueryKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (_registrations.TryGetValue(key, out var registration))
        {
            await PersistRegistrationAsync(registration, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Persists the current data of every registered query that currently holds data.</summary>
    public async Task PersistAllAsync(CancellationToken cancellationToken = default)
    {
        foreach (var registration in _registrations.Values)
        {
            await PersistRegistrationAsync(registration, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Restores a single registered query from the persister into the client.
    /// </summary>
    /// <returns><see langword="true"/> if persisted data was found and applied.</returns>
    public async Task<bool> RestoreAsync(QueryKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!_registrations.TryGetValue(key, out var registration))
        {
            return false;
        }

        var persisted = await _persister.LoadAsync(registration.StorageKey, cancellationToken).ConfigureAwait(false);
        return persisted is not null && await ApplyAsync(registration, persisted, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Restores every registered query for which (unexpired, compatible) persisted data exists.</summary>
    /// <returns>The number of queries restored.</returns>
    public async Task<int> RestoreAllAsync(CancellationToken cancellationToken = default)
    {
        var persisted = await _persister.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        var byKey = new Dictionary<string, PersistedQuery>(StringComparer.Ordinal);
        foreach (var entry in persisted)
        {
            byKey[entry.Key] = entry;
        }

        var restored = 0;
        foreach (var registration in _registrations.Values)
        {
            if (byKey.TryGetValue(registration.StorageKey, out var entry) &&
                await ApplyAsync(registration, entry, cancellationToken).ConfigureAwait(false))
            {
                restored++;
            }
        }

        return restored;
    }

    /// <summary>
    /// Starts saving registered queries automatically whenever their data changes (successful fetches,
    /// <c>SetQueryData</c>, optimistic updates). Changes are coalesced and written after
    /// <paramref name="delay"/> (default one second) to avoid write storms. Dispose the manager (or the
    /// returned handle) to stop.
    /// </summary>
    public IDisposable StartAutoPersist(TimeSpan? delay = null)
    {
        lock (_autoGate)
        {
            if (_autoHandler is null)
            {
                _autoDelay = delay is { } d && d > TimeSpan.Zero ? d : TimeSpan.FromSeconds(1);
                _autoHandler = OnCacheChanged;
                _client.Cache.Changed += _autoHandler;
            }
        }

        return new AutoPersistHandle(this);
    }

    /// <summary>Writes any changes still waiting for the auto-persist delay immediately.</summary>
    public Task FlushAutoPersistAsync(CancellationToken cancellationToken = default)
    {
        lock (_autoGate)
        {
            _autoTimer?.Dispose();
            _autoTimer = null;
        }

        return PersistDirtyAsync(cancellationToken);
    }

    private void OnCacheChanged(QueryCacheChange change)
    {
        if (change.Kind is not QueryCacheChangeKind.DataUpdated || !_registrations.ContainsKey(change.Key))
        {
            return;
        }

        lock (_autoGate)
        {
            if (_autoHandler is null)
            {
                return;
            }

            _dirty.Add(change.Key);
            _autoTimer ??= _timeProvider.CreateTimer(
                static state => ((QueryPersistenceManager)state!).OnAutoTimer(),
                this,
                _autoDelay,
                Timeout.InfiniteTimeSpan);
        }
    }

    private void OnAutoTimer()
    {
        lock (_autoGate)
        {
            _autoTimer?.Dispose();
            _autoTimer = null;
            _autoFlush = _autoFlush.ContinueWith(
                static (_, state) => ((QueryPersistenceManager)state!).PersistDirtyAsync(CancellationToken.None),
                this,
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
        }
    }

    private async Task PersistDirtyAsync(CancellationToken cancellationToken)
    {
        QueryKey[] keys;
        lock (_autoGate)
        {
            keys = [.. _dirty];
            _dirty.Clear();
        }

        foreach (var key in keys)
        {
            if (_registrations.TryGetValue(key, out var registration))
            {
                await PersistRegistrationAsync(registration, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void StopAutoPersist()
    {
        lock (_autoGate)
        {
            if (_autoHandler is not null)
            {
                _client.Cache.Changed -= _autoHandler;
                _autoHandler = null;
            }

            _autoTimer?.Dispose();
            _autoTimer = null;
        }
    }

    private async Task<bool> ApplyAsync(Registration registration, PersistedQuery persisted, CancellationToken cancellationToken)
    {
        if (_options.MaxAge is { } maxAge && _timeProvider.GetUtcNow() - persisted.UpdatedAt > maxAge)
        {
            await DeleteQuietlyAsync(registration, cancellationToken).ConfigureAwait(false);
            return false;
        }

        try
        {
            registration.Apply(persisted.Payload, persisted.UpdatedAt);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // The payload no longer matches the type (schema change): drop it rather than failing startup.
            RaiseError(registration.Key, ex);
            await DeleteQuietlyAsync(registration, cancellationToken).ConfigureAwait(false);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Anything else (for example the key is already cached with a different type) is a wiring problem
            // in this session, not bad data: report it and keep the persisted record.
            RaiseError(registration.Key, ex);
            return false;
        }
    }

    private async Task DeleteQuietlyAsync(Registration registration, CancellationToken cancellationToken)
    {
        try
        {
            await _persister.RemoveAsync(registration.StorageKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RaiseError(registration.Key, ex);
        }
    }

    private async Task PersistRegistrationAsync(Registration registration, CancellationToken cancellationToken)
    {
        // One write per key at a time, with the snapshot taken inside the gate: a slow older write can never
        // land after (and overwrite) a newer one.
        await registration.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (registration.Snapshot() is not { } snapshot)
            {
                return;
            }

            await _persister.SaveAsync(
                new PersistedQuery(registration.StorageKey, snapshot.Payload, snapshot.UpdatedAt),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RaiseError(registration.Key, ex);
        }
        finally
        {
            registration.Gate.Release();
        }
    }

    private string StorageKeyFor(QueryKey key) =>
        _options.Buster is { Length: > 0 } buster ? buster + "|" + key : key.ToString();

    private void RaiseError(QueryKey key, Exception error)
    {
        try
        {
            Error?.Invoke(key, error);
        }
        catch (Exception)
        {
            // Subscriber errors must not break persistence.
        }
    }

    /// <summary>Stops auto-persisting. Does not flush pending changes; call <see cref="FlushAutoPersistAsync"/> first if needed.</summary>
    public void Dispose() => StopAutoPersist();

    private sealed record Registration(
        QueryKey Key,
        string StorageKey,
        Func<(string Payload, DateTimeOffset UpdatedAt)?> Snapshot,
        Action<string, DateTimeOffset> Apply,
        SemaphoreSlim Gate);

    private sealed class AutoPersistHandle(QueryPersistenceManager owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.StopAutoPersist();
            }
        }
    }
}
