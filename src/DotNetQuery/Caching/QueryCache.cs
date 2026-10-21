using System.Collections.Concurrent;

namespace DotNetQuery;

/// <summary>
/// Thread-safe store of query cache entries keyed by <see cref="QueryKey"/>.
/// Supports typed entry retrieval, hierarchical lookup for invalidation, change notifications, and
/// garbage collection of expired inactive entries.
/// </summary>
public sealed class QueryCache
{
    private readonly ConcurrentDictionary<QueryKey, IQueryEntry> _entries = new();
    private readonly QueryRuntime _runtime;

    /// <summary>Initializes a new cache using the supplied time source.</summary>
    /// <param name="timeProvider">Time source used for staleness and GC. Defaults to <see cref="TimeProvider.System"/>.</param>
    public QueryCache(TimeProvider? timeProvider = null)
        : this(timeProvider, null, null, TimeSpan.FromMinutes(5))
    {
    }

    internal QueryCache(TimeProvider? timeProvider, IQueryEventSink? eventSink, IConnectivityProvider? connectivity, TimeSpan defaultCacheTime)
    {
        _runtime = new QueryRuntime(this, timeProvider ?? TimeProvider.System, eventSink, connectivity, defaultCacheTime);
    }

    /// <summary>
    /// Raised whenever an entry is added or removed, or its state changes. Handlers run synchronously on the
    /// thread that made the change and must be fast; exceptions they throw are swallowed. Useful for
    /// inspection tooling and for persisting data as it changes.
    /// </summary>
    public event Action<QueryCacheChange>? Changed;

    /// <summary>Gets the number of entries currently held in the cache.</summary>
    public int Count => _entries.Count;

    /// <summary>Gets the keys of all entries currently in the cache.</summary>
    public IReadOnlyCollection<QueryKey> Keys => _entries.Keys.ToArray();

    /// <summary>
    /// Produces a non-generic snapshot describing every entry in the cache, for inspection tooling.
    /// </summary>
    public IReadOnlyList<QueryDescriptor> Describe()
    {
        var descriptors = new List<QueryDescriptor>(_entries.Count);
        foreach (var entry in _entries.Values)
        {
            descriptors.Add(entry.Describe());
        }

        return descriptors;
    }

    /// <summary>Describes a single entry, or returns <see langword="null"/> if no entry exists for <paramref name="key"/>.</summary>
    public QueryDescriptor? Describe(QueryKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _entries.TryGetValue(key, out var entry) ? entry.Describe() : null;
    }

    internal QueryRuntime Runtime => _runtime;

    internal QueryEntry<T> GetOrCreate<T>(QueryKey key)
    {
        while (true)
        {
            QueryEntry<T>? created = null;
            var entry = _entries.GetOrAdd(key, k => created = new QueryEntry<T>(k, _runtime));

            if (entry.IsRemoved)
            {
                // Lost a race with garbage collection or removal; drop the dead entry and retry.
                _entries.TryRemove(KeyValuePair.Create(key, entry));
                continue;
            }

            if (entry is not QueryEntry<T> typed)
            {
                throw new InvalidOperationException(
                    $"Query {key} was previously used with a different data type ({entry.Describe().DataTypeName}). " +
                    $"A given key must always map to the same type (requested {TypeKeyName<T>.Value}).");
            }

            if (ReferenceEquals(entry, created))
            {
                RaiseChanged(key, QueryCacheChangeKind.Added);
            }

            return typed;
        }
    }

    /// <summary>Runs <paramref name="action"/> against the live entry, retrying if it is collected concurrently.</summary>
    internal TResult WithEntry<T, TResult>(QueryKey key, Func<QueryEntry<T>, TResult?> action)
        where TResult : class
    {
        while (true)
        {
            if (action(GetOrCreate<T>(key)) is { } result)
            {
                return result;
            }
        }
    }

    internal bool TryGetEntry(QueryKey key, out IQueryEntry entry) => _entries.TryGetValue(key, out entry!);

    internal IReadOnlyList<IQueryEntry> GetEntries() => _entries.Values.ToArray();

    /// <summary>Attempts to get the current state of a query without triggering a fetch.</summary>
    /// <returns><see langword="true"/> if an entry of type <typeparamref name="T"/> exists for <paramref name="key"/>.</returns>
    public bool TryGetState<T>(QueryKey key, out QueryState<T> state)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (_entries.TryGetValue(key, out var entry) && entry is QueryEntry<T> typed)
        {
            state = typed.CurrentState;
            return true;
        }

        state = QueryState<T>.Idle;
        return false;
    }

    /// <summary>Removes the entry for the specified key, cancelling any in-flight fetch.</summary>
    /// <returns><see langword="true"/> if an entry was removed.</returns>
    public bool Remove(QueryKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (_entries.TryRemove(key, out var entry))
        {
            entry.Dispose();
            RaiseChanged(key, QueryCacheChangeKind.Removed);
            return true;
        }

        return false;
    }

    /// <summary>Removes every entry, cancelling all in-flight fetches.</summary>
    public void Clear()
    {
        foreach (var key in _entries.Keys)
        {
            Remove(key);
        }
    }

    /// <summary>
    /// Marks every entry whose key matches <paramref name="predicate"/> as invalidated, so that the
    /// next fetch reloads it from the origin.
    /// </summary>
    /// <returns>The matched entries, flagged with whether each should be refetched now.</returns>
    internal IReadOnlyList<(IQueryEntry Entry, bool Refetch)> Invalidate(Func<QueryKey, bool> predicate)
    {
        var matched = new List<(IQueryEntry, bool)>();
        foreach (var (key, entry) in _entries)
        {
            if (predicate(key))
            {
                matched.Add((entry, entry.Invalidate()));
            }
        }

        return matched;
    }

    /// <summary>
    /// Removes inactive entries (no observers, no in-flight fetch) whose retention window has elapsed.
    /// Runs automatically on the client's <see cref="QueryClientOptions.GarbageCollectionInterval"/>.
    /// </summary>
    /// <returns>The number of entries collected.</returns>
    public int CollectGarbage()
    {
        var now = _runtime.TimeProvider.GetUtcNow();
        var collected = 0;
        foreach (var (key, entry) in _entries)
        {
            if (entry.TryCollect(now) && _entries.TryRemove(KeyValuePair.Create(key, entry)))
            {
                collected++;
                RaiseChanged(key, QueryCacheChangeKind.Removed);
            }
        }

        return collected;
    }

    internal void RaiseChanged(QueryKey key, QueryCacheChangeKind kind)
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        var change = new QueryCacheChange(key, kind);
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<QueryCacheChange>)handler)(change);
            }
            catch (Exception)
            {
                // Isolate subscribers: a faulty listener must not break cache operations.
            }
        }
    }
}

/// <summary>The kind of change reported by <see cref="QueryCache.Changed"/>.</summary>
public enum QueryCacheChangeKind
{
    /// <summary>A new entry was added to the cache.</summary>
    Added,

    /// <summary>An entry was removed (explicitly, by clearing, or by garbage collection).</summary>
    Removed,

    /// <summary>An entry's state changed without new data (for example loading, error, or paused).</summary>
    StateChanged,

    /// <summary>An entry received new data (a successful fetch, <c>SetQueryData</c>, or hydration).</summary>
    DataUpdated,
}

/// <summary>Describes a single change to the <see cref="QueryCache"/>.</summary>
/// <param name="Key">The key of the affected entry.</param>
/// <param name="Kind">What changed.</param>
public readonly record struct QueryCacheChange(QueryKey Key, QueryCacheChangeKind Kind);
