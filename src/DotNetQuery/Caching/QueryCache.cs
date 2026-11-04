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
        : this(timeProvider, null, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5))
    {
    }

    internal QueryCache(
        TimeProvider? timeProvider,
        IQueryEventSink? eventSink,
        IConnectivityProvider? connectivity,
        TimeSpan defaultCacheTime,
        TimeSpan mutationCacheTime,
        QueryClientOptions? options = null)
    {
        _runtime = new QueryRuntime(this, timeProvider ?? TimeProvider.System, eventSink, connectivity, defaultCacheTime, options);
        Mutations = new MutationCache(_runtime.TimeProvider, mutationCacheTime);
        if (options?.TrimOnMemoryPressure == true)
        {
            Gen2GcCallback.Register(static target => ((QueryCache)target).OnGen2Collection(), this);
        }
    }

    // Runs on the finalizer thread after a gen-2 collection: check the memory load, trim on the thread pool.
    private bool OnGen2Collection()
    {
        var info = GC.GetGCMemoryInfo();
        if (info.HighMemoryLoadThresholdBytes > 0 && info.MemoryLoadBytes >= info.HighMemoryLoadThresholdBytes)
        {
            ThreadPool.UnsafeQueueUserWorkItem(static cache => cache.Trim(0), this, preferLocal: false);
        }

        return true;
    }

    /// <summary>
    /// Evicts the least recently used inactive queries (no observers, no fetch in flight or deferred) until at most
    /// <paramref name="maxInactiveQueries"/> remain, regardless of their cache time. Runs automatically when
    /// <see cref="QueryClientOptions.MaxInactiveQueries"/> or <see cref="QueryClientOptions.TrimOnMemoryPressure"/> is set.
    /// </summary>
    /// <returns>The number of queries evicted.</returns>
    public int Trim(int maxInactiveQueries)
    {
        var inactive = _entries.Values.Where(e => !e.IsRemoved && !e.IsActive).OrderBy(e => e.LastActivityAt).ToList();
        var excess = inactive.Count - Math.Max(0, maxInactiveQueries);
        var evicted = 0;
        foreach (var entry in inactive)
        {
            if (evicted >= excess)
            {
                break;
            }

            if (entry.TryEvict() && _entries.TryRemove(KeyValuePair.Create(entry.Key, entry)))
            {
                evicted++;
                RaiseChanged(entry.Key, QueryCacheChangeKind.Removed);
            }
        }

        return evicted;
    }

    /// <summary>
    /// Captures the current state of every query matching <paramref name="filter"/> (all queries when
    /// <see langword="null"/>) for a later <see cref="QueryCacheSnapshot.Restore"/>.
    /// </summary>
    public QueryCacheSnapshot CreateSnapshot(QueryFilter? filter = null)
    {
        var entries = new List<EntrySnapshot>();
        foreach (var entry in FindEntries(filter))
        {
            if (entry.CaptureSnapshot() is { } snapshot)
            {
                entries.Add(snapshot);
            }
        }

        return new QueryCacheSnapshot(this, entries, _runtime.TimeProvider.GetUtcNow());
    }

    /// <summary>Gets the client's mutation runs: pending ones and recently settled ones.</summary>
    public MutationCache Mutations { get; }

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

    /// <summary>
    /// Describes every entry that matches <paramref name="filter"/> (every entry when <see langword="null"/>), for
    /// inspection tooling and bulk reads.
    /// </summary>
    public IReadOnlyList<QueryDescriptor> FindAll(QueryFilter? filter = null)
    {
        var descriptors = new List<QueryDescriptor>();
        foreach (var entry in FindEntries(filter))
        {
            descriptors.Add(entry.Describe());
        }

        return descriptors;
    }

    internal QueryRuntime Runtime => _runtime;

    internal List<IQueryEntry> FindEntries(QueryFilter? filter)
    {
        var matched = new List<IQueryEntry>();
        foreach (var entry in _entries.Values)
        {
            if (filter is null ? !entry.IsRemoved : entry.Matches(filter))
            {
                matched.Add(entry);
            }
        }

        return matched;
    }

    /// <summary>
    /// Removes <paramref name="entry"/> if it is still the live entry for its key and nothing observes it. An observed
    /// entry is reset in place instead (data discarded, in-flight fetch cancelled) and refetched for its observers,
    /// so they keep receiving updates rather than being stranded on a dead entry.
    /// </summary>
    internal bool Remove(IQueryEntry entry)
    {
        if (entry.TryRemoveUnobserved())
        {
            // A concurrent GetOrCreate may evict the dead entry first; it then reports the removal itself.
            if (_entries.TryRemove(KeyValuePair.Create(entry.Key, entry)))
            {
                RaiseChanged(entry.Key, QueryCacheChangeKind.Removed);
            }

            return true;
        }

        var cancelDetached = entry.ResetAndDetach();
        _ = entry.RefetchAsync(FetchMode.CancelAndRefetch, includeDisabled: false, CancellationToken.None);
        cancelDetached();
        return true;
    }

    /// <summary>Removes and disposes every entry, observed or not (used when the owning client is disposed).</summary>
    internal void DisposeAll()
    {
        foreach (var (key, entry) in _entries)
        {
            if (_entries.TryRemove(KeyValuePair.Create(key, entry)))
            {
                entry.Dispose();
                RaiseChanged(key, QueryCacheChangeKind.Removed);
            }
        }
    }

    internal QueryEntry<T> GetOrCreate<T>(QueryKey key) =>
        // Fast path for the common case - the entry exists - without the closure the creating path needs.
        _entries.TryGetValue(key, out var existing) && existing is QueryEntry<T> { IsRemoved: false } live
            ? live
            : GetOrCreateSlow<T>(key);

    private QueryEntry<T> GetOrCreateSlow<T>(QueryKey key)
    {
        while (true)
        {
            QueryEntry<T>? created = null;
            var entry = _entries.GetOrAdd(key, k => created = new QueryEntry<T>(k, _runtime));

            if (entry.IsRemoved)
            {
                // Lost a race with garbage collection or removal; drop the dead entry (reporting the removal if
                // this thread is the one that evicts it) and retry.
                if (_entries.TryRemove(KeyValuePair.Create(key, entry)))
                {
                    RaiseChanged(key, QueryCacheChangeKind.Removed);
                }

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

    // Hot-path variant: the state travels as an argument, so a static lambda needs no closure allocation per call.
    internal TResult WithEntry<T, TState, TResult>(QueryKey key, TState state, Func<QueryEntry<T>, TState, TResult?> action)
        where TResult : class
    {
        while (true)
        {
            if (action(GetOrCreate<T>(key), state) is { } result)
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

    /// <summary>
    /// Removes the entry for the specified key, cancelling any in-flight fetch. An entry that is still observed (for
    /// example rendered by a component) is not dropped - that would strand its observers - but reset: its data is
    /// discarded and it is refetched for them.
    /// </summary>
    /// <returns><see langword="true"/> if an entry was removed (or, when observed, reset).</returns>
    public bool Remove(QueryKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _entries.TryGetValue(key, out var entry) && Remove(entry);
    }

    /// <summary>
    /// Removes every entry, cancelling all in-flight fetches. Observed entries are reset and refetched instead (see
    /// <see cref="Remove(QueryKey)"/>), so no cached data survives and every rendered query reloads.
    /// </summary>
    public void Clear()
    {
        foreach (var entry in _entries.Values)
        {
            Remove(entry);
        }
    }

    /// <summary>
    /// Marks every entry whose key matches <paramref name="predicate"/> as invalidated, so that the
    /// next fetch reloads it from the origin.
    /// </summary>
    /// <returns>The matched entries, flagged with whether each should be refetched now.</returns>
    internal IReadOnlyList<(IQueryEntry Entry, bool Refetch)> Invalidate(Func<QueryKey, bool> predicate) =>
        Invalidate(entry => predicate(entry.Key));

    /// <inheritdoc cref="Invalidate(Func{QueryKey, bool})"/>
    /// <param name="predicate">Selects the entries to invalidate.</param>
    /// <param name="requested">
    /// The key prefixes the caller asked to invalidate, if known: declared invalidation dependencies of these apply
    /// even when nothing under them is cached.
    /// </param>
    /// <param name="changeSequence">The logical time of the change that prompted the invalidation (see <see cref="IQueryEntry.Invalidate"/>).</param>
    internal IReadOnlyList<(IQueryEntry Entry, bool Refetch)> Invalidate(Func<IQueryEntry, bool> predicate, IReadOnlyList<QueryKey>? requested = null, long changeSequence = long.MaxValue)
    {
        var matched = new List<(IQueryEntry, bool)>();
        var seen = new HashSet<IQueryEntry>(ReferenceEqualityComparer.Instance);
        foreach (var entry in _entries.Values)
        {
            if (predicate(entry) && seen.Add(entry))
            {
                matched.Add((entry, entry.Invalidate(changeSequence)));
            }
        }

        // Follow declared invalidation dependencies (transitively; each dependent prefix is expanded once).
        if ((matched.Count > 0 || requested is { Count: > 0 }) && _runtime.Options is { HasInvalidationDependencies: true } options)
        {
            var expanded = new HashSet<QueryKey>();
            var frontier = new Queue<(QueryKey Key, bool Requested)>(matched.Select(m => (m.Item1.Key, false)));
            foreach (var key in requested ?? [])
            {
                frontier.Enqueue((key, true));
            }

            while (frontier.TryDequeue(out var item))
            {
                foreach (var dependent in options.GetInvalidationDependents(item.Key, includeNarrowerSources: item.Requested))
                {
                    if (!expanded.Add(dependent))
                    {
                        continue;
                    }

                    frontier.Enqueue((dependent, true));
                    foreach (var entry in _entries.Values)
                    {
                        if (dependent.IsPrefixOf(entry.Key) && seen.Add(entry))
                        {
                            matched.Add((entry, entry.Invalidate(changeSequence)));
                            frontier.Enqueue((entry.Key, false));
                        }
                    }
                }
            }
        }

        if (matched.Count > 0)
        {
            _runtime.EventSink?.QueriesInvalidated(matched.Select(m => m.Item1.Key).ToArray());
        }

        return matched;
    }

    /// <summary>
    /// Removes inactive entries (no observers, no in-flight fetch) whose retention window has elapsed, and settled
    /// mutation runs older than <see cref="QueryClientOptions.MutationCacheTime"/>. Runs automatically on the
    /// client's <see cref="QueryClientOptions.GarbageCollectionInterval"/>.
    /// </summary>
    /// <returns>The number of query entries collected.</returns>
    public int CollectGarbage()
    {
        Mutations.CollectGarbage();
        if (_runtime.Options?.MaxInactiveQueries is { } maxInactive)
        {
            Trim(maxInactive);
        }

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
