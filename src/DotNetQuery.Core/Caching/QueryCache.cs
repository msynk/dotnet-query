using System.Collections.Concurrent;

namespace DotNetQuery;

/// <summary>
/// Thread-safe store of query cache entries keyed by <see cref="QueryKey"/>.
/// Supports typed entry retrieval, hierarchical lookup for invalidation, and
/// garbage collection of expired inactive entries.
/// </summary>
public sealed class QueryCache
{
    private readonly ConcurrentDictionary<QueryKey, IQueryEntry> _entries = new();
    private readonly TimeProvider _timeProvider;
    private readonly IQueryEventSink? _eventSink;

    /// <summary>Initializes a new cache using the supplied time source.</summary>
    /// <param name="timeProvider">Time source used for staleness and GC. Defaults to <see cref="TimeProvider.System"/>.</param>
    public QueryCache(TimeProvider? timeProvider = null)
        : this(timeProvider, null)
    {
    }

    internal QueryCache(TimeProvider? timeProvider, IQueryEventSink? eventSink)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _eventSink = eventSink;
    }

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

    internal QueryEntry<T> GetOrCreate<T>(QueryKey key)
    {
        var entry = _entries.GetOrAdd(key, k => new QueryEntry<T>(k, _timeProvider, _eventSink));
        if (entry is QueryEntry<T> typed)
        {
            return typed;
        }

        throw new InvalidOperationException(
            $"Query {key} was previously used with a different data type ({entry.GetType()}). " +
            $"A given key must always map to the same type.");
    }

    internal bool TryGetEntry(QueryKey key, out IQueryEntry entry) => _entries.TryGetValue(key, out entry!);

    internal IReadOnlyList<IQueryEntry> GetEntries() => _entries.Values.ToArray();

    /// <summary>Attempts to get the current state of a query without triggering a fetch.</summary>
    /// <returns><see langword="true"/> if an entry exists for <paramref name="key"/>.</returns>
    public bool TryGetState<T>(QueryKey key, out QueryState<T> state)
    {
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
        if (_entries.TryRemove(key, out var entry))
        {
            entry.Dispose();
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
    /// Marks every entry whose key matches <paramref name="predicate"/> as stale, so that the
    /// next fetch (or an active observer's refetch) reloads it from the origin.
    /// </summary>
    /// <returns>The keys of the entries that were marked stale.</returns>
    internal IReadOnlyList<QueryKey> MarkMatchingStale(Func<QueryKey, bool> predicate)
    {
        var matched = new List<QueryKey>();
        foreach (var (key, entry) in _entries)
        {
            if (predicate(key))
            {
                entry.MarkStale();
                matched.Add(key);
            }
        }

        return matched;
    }

    /// <summary>
    /// Removes inactive entries (no observers) whose retention window has elapsed.
    /// </summary>
    /// <returns>The number of entries collected.</returns>
    public int CollectGarbage()
    {
        var now = _timeProvider.GetUtcNow();
        var collected = 0;
        foreach (var (key, entry) in _entries)
        {
            if (!entry.IsActive && now - entry.LastActivityAt >= entry.CacheTime)
            {
                if (_entries.TryRemove(key, out var removed))
                {
                    removed.Dispose();
                    collected++;
                }
            }
        }

        return collected;
    }
}
