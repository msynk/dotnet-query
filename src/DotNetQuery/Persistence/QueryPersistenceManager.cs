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

    /// <summary>
    /// Upgrades data persisted under an older <see cref="Buster"/> instead of discarding it: maps the old buster (use
    /// <c>""</c> for data persisted without one) to a function that rewrites a payload into the current shape - or
    /// returns <see langword="null"/> to drop it. Migrated records are re-saved under the current buster and the old
    /// ones deleted. Chain versions inside the function (v1 to v2 to v3) if needed.
    /// </summary>
    /// <example>
    /// <code>
    /// new QueryPersistenceOptions
    /// {
    ///     Buster = "v3",
    ///     Migrations = new Dictionary&lt;string, Func&lt;string, string?&gt;&gt;
    ///     {
    ///         ["v2"] = payload =&gt; payload.Replace(""name":", ""title":"),
    ///     },
    /// }
    /// </code>
    /// </example>
    public IReadOnlyDictionary<string, Func<string, string?>>? Migrations { get; init; }
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
    private readonly ConcurrentDictionary<QueryKey, Registration> _dynamic = new();
    private readonly List<PrefixRegistration> _prefixes = [];
    private readonly ConcurrentDictionary<string, PersistedQuery> _pendingRestore = new(StringComparer.Ordinal);
    private Action<QueryCacheChange>? _restoreHandler;
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

    /// <summary>Gets the keys registered for persistence (exact registrations; see <see cref="RegisteredPrefixes"/>).</summary>
    public IReadOnlyCollection<QueryKey> RegisteredKeys => _registrations.Keys.ToArray();

    /// <summary>Gets the key prefixes registered with <see cref="RegisterPrefix{T}(QueryKey, JsonTypeInfo{T})"/>.</summary>
    public IReadOnlyCollection<QueryKey> RegisteredPrefixes
    {
        get { lock (_prefixes) { return _prefixes.Select(p => p.Prefix).ToArray(); } }
    }

    /// <summary>
    /// Persists every query of type <typeparamref name="T"/> whose key starts with <paramref name="prefix"/> - for
    /// dynamic keys such as <c>["todo", id]</c> that cannot be registered one by one. Serialized with
    /// <paramref name="typeInfo"/> (trim/AOT-safe).
    /// </summary>
    /// <remarks>
    /// A key cannot be rebuilt from its persisted text, so <see cref="RestoreAllAsync"/> applies a prefixed record
    /// when a matching query exists in the cache and otherwise keeps it ready: the moment the app first creates that
    /// query (by fetching or watching it), the record is hydrated synchronously - before the first fetch decides
    /// whether the data is fresh. Exact registrations take precedence over prefixes.
    /// </remarks>
    public QueryPersistenceManager RegisterPrefix<T>(QueryKey prefix, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(typeInfo);
        return AddPrefix(prefix, data => JsonSerializer.Serialize(data, typeInfo), payload => JsonSerializer.Deserialize(payload, typeInfo)!);
    }

    /// <summary>Persists every query of type <typeparamref name="T"/> under <paramref name="prefix"/>, using reflection-based JSON.</summary>
    [RequiresUnreferencedCode("JSON serialization of T may require types that cannot be statically analyzed. Use the JsonTypeInfo<T> overload.")]
    [RequiresDynamicCode("JSON serialization of T may require runtime code generation. Use the JsonTypeInfo<T> overload.")]
    public QueryPersistenceManager RegisterPrefix<T>(QueryKey prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        var options = _jsonOptions;
        return AddPrefix(prefix, data => JsonSerializer.Serialize(data, options), payload => JsonSerializer.Deserialize<T>(payload, options)!);
    }

    private QueryPersistenceManager AddPrefix<T>(QueryKey prefix, Func<T, string> serialize, Func<string, T> deserialize)
    {
        var registration = new PrefixRegistration(prefix, key =>
            _client.Cache.TryGetEntry(key, out var entry) && entry is QueryEntry<T>
                ? Create(key, serialize, deserialize)
                : null);
        lock (_prefixes)
        {
            _prefixes.Add(registration);
        }

        EnsurePrefixHandler();
        return this;
    }

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
        _registrations[key] = Create(key, serialize, deserialize);
        return this;
    }

    private Registration Create<T>(QueryKey key, Func<T, string> serialize, Func<string, T> deserialize)
    {
        return new Registration(
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
    }

    // The exact registration for a key, else a (cached) registration derived from a matching prefix whose type
    // matches the cached entry.
    private Registration? Resolve(QueryKey key)
    {
        if (_registrations.TryGetValue(key, out var exact))
        {
            return exact;
        }

        if (_dynamic.TryGetValue(key, out var dynamic))
        {
            return dynamic;
        }

        PrefixRegistration[] prefixes;
        lock (_prefixes)
        {
            prefixes = [.. _prefixes];
        }

        foreach (var prefix in prefixes)
        {
            if (prefix.Prefix.IsPrefixOf(key) && prefix.Create(key) is { } created)
            {
                return _dynamic.GetOrAdd(key, created);
            }
        }

        return null;
    }

    private bool MatchesPrefix(string storageKey)
    {
        lock (_prefixes)
        {
            foreach (var prefix in _prefixes)
            {
                // QueryKey text is "[seg, seg, ...]": a key under the prefix starts with the prefix text minus its
                // closing bracket, followed by ", " or "]".
                var head = StorageKeyFor(prefix.Prefix)[..^1];
                if (storageKey.StartsWith(head, StringComparison.Ordinal) &&
                    storageKey.Length > head.Length && (storageKey[head.Length] == ']' || storageKey.AsSpan(head.Length).StartsWith(", ", StringComparison.Ordinal)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Persists a single registered query's current data.</summary>
    public async Task PersistAsync(QueryKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (Resolve(key) is { } registration)
        {
            await PersistRegistrationAsync(registration, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Persists the current data of every registered query (and every cached query under a registered prefix) that holds data.</summary>
    public async Task PersistAllAsync(CancellationToken cancellationToken = default)
    {
        foreach (var registration in _registrations.Values)
        {
            await PersistRegistrationAsync(registration, cancellationToken).ConfigureAwait(false);
        }

        foreach (var key in _client.Cache.Keys)
        {
            if (!_registrations.ContainsKey(key) && Resolve(key) is { } registration)
            {
                await PersistRegistrationAsync(registration, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Restores a single registered query from the persister into the client.
    /// </summary>
    /// <returns><see langword="true"/> if persisted data was found and applied.</returns>
    public async Task<bool> RestoreAsync(QueryKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (Resolve(key) is not { } registration)
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

        await MigrateAsync(byKey, cancellationToken).ConfigureAwait(false);

        var restored = 0;
        foreach (var registration in _registrations.Values)
        {
            if (byKey.Remove(registration.StorageKey, out var entry) &&
                await ApplyAsync(registration, entry, cancellationToken).ConfigureAwait(false))
            {
                restored++;
            }
        }

        // Prefixed records: keep them for the first use of their query, then apply those whose query already exists.
        // The first-use handler is subscribed before the cache is inspected, so a query created in between is caught
        // by one or the other (a record is consumed only once).
        var prefixed = byKey.Where(pair => MatchesPrefix(pair.Key)).ToArray();
        foreach (var (storageKey, entry) in prefixed)
        {
            _pendingRestore[storageKey] = entry;
        }

        if (prefixed.Length > 0)
        {
            EnsurePrefixHandler();
        }

        var keysByText = _client.Cache.Keys.ToDictionary(StorageKeyFor, k => k, StringComparer.Ordinal);
        foreach (var (storageKey, _) in prefixed)
        {
            if (keysByText.TryGetValue(storageKey, out var existing) && Resolve(existing) is { } registration &&
                _pendingRestore.TryRemove(storageKey, out var record) &&
                await ApplyAsync(registration, record, cancellationToken).ConfigureAwait(false))
            {
                restored++;
            }
        }

        return restored;
    }

    // Rewrites records persisted under another buster that has a migration into the current storage keys.
    private async Task MigrateAsync(Dictionary<string, PersistedQuery> byKey, CancellationToken cancellationToken)
    {
        if (_options.Migrations is not { Count: > 0 } migrations)
        {
            return;
        }

        var current = _options.Buster ?? string.Empty;
        foreach (var record in byKey.Values.ToArray())
        {
            var separator = record.Key.IndexOf("|[", StringComparison.Ordinal);
            var buster = separator >= 0 ? record.Key[..separator] : string.Empty;
            var keyText = separator >= 0 ? record.Key[(separator + 1)..] : record.Key;
            if (buster == current || !keyText.StartsWith('[') || !migrations.TryGetValue(buster, out var migrate))
            {
                continue;
            }

            byKey.Remove(record.Key);
            string? payload;
            try
            {
                payload = migrate(record.Payload);
            }
            catch (Exception ex)
            {
                RaiseError(QueryKey.Create(keyText), ex);
                payload = null;
            }

            try
            {
                var target = current.Length > 0 ? current + "|" + keyText : keyText;
                if (payload is not null && !byKey.ContainsKey(target))
                {
                    var migrated = new PersistedQuery(target, payload, record.UpdatedAt);
                    await _persister.SaveAsync(migrated, cancellationToken).ConfigureAwait(false);
                    byKey[target] = migrated;
                }

                await _persister.RemoveAsync(record.Key, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RaiseError(QueryKey.Create(keyText), ex);
            }
        }
    }

    private void EnsurePrefixHandler()
    {
        lock (_autoGate)
        {
            if (_restoreHandler is null)
            {
                _restoreHandler = OnPrefixedEntryChanged;
                _client.Cache.Changed += _restoreHandler;
            }
        }
    }

    // Added: runs synchronously while the entry is being created, so the record is hydrated before the first fetch
    // checks freshness (persisted data older than MaxAge is dropped instead). Removed: forgets the prefix-derived
    // registration of the evicted query.
    private void OnPrefixedEntryChanged(QueryCacheChange change)
    {
        if (change.Kind is QueryCacheChangeKind.Removed)
        {
            _dynamic.TryRemove(change.Key, out _);
            return;
        }

        if (change.Kind is not QueryCacheChangeKind.Added || _pendingRestore.IsEmpty)
        {
            return;
        }

        // Resolve first: a record whose query was created with another type stays pending for a later entry.
        var storageKey = StorageKeyFor(change.Key);
        if (!_pendingRestore.ContainsKey(storageKey) || Resolve(change.Key) is not { } registration ||
            !_pendingRestore.TryRemove(storageKey, out var record))
        {
            return;
        }

        if (_options.MaxAge is { } maxAge && _timeProvider.GetUtcNow() - record.UpdatedAt > maxAge)
        {
            _ = DeleteQuietlyAsync(registration, CancellationToken.None);
            return;
        }

        try
        {
            registration.Apply(record.Payload, record.UpdatedAt);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            RaiseError(registration.Key, ex);
            _ = DeleteQuietlyAsync(registration, CancellationToken.None);
        }
        catch (Exception ex)
        {
            RaiseError(registration.Key, ex);
        }
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
        if (change.Kind is QueryCacheChangeKind.Removed)
        {
            _dynamic.TryRemove(change.Key, out _); // forget prefix-derived registrations of evicted queries
            return;
        }

        if (change.Kind is not QueryCacheChangeKind.DataUpdated || Resolve(change.Key) is null)
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
            if (Resolve(key) is { } registration)
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

    /// <summary>
    /// Stops auto-persisting and restoring prefixed records on first use. Does not flush pending changes; call
    /// <see cref="FlushAutoPersistAsync"/> first if needed.
    /// </summary>
    public void Dispose()
    {
        StopAutoPersist();
        lock (_autoGate)
        {
            if (_restoreHandler is not null)
            {
                _client.Cache.Changed -= _restoreHandler;
                _restoreHandler = null;
            }
        }
    }

    private sealed record PrefixRegistration(QueryKey Prefix, Func<QueryKey, Registration?> Create);

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
