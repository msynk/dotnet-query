namespace DotNetQuery;

/// <summary>
/// An in-memory, point-in-time copy of cached queries (data, timestamps, status, staleness), created with
/// <see cref="QueryCache.CreateSnapshot"/>. Restore it to roll the cache back - for example to undo a batch of local
/// edits, to reset a demo, or to step back in time while debugging (DevTools "time travel").
/// </summary>
/// <remarks>
/// The snapshot holds references to the cached values, not copies: treat cached data as immutable (the library's
/// contract) and a snapshot stays exact.
/// </remarks>
public sealed class QueryCacheSnapshot
{
    private readonly QueryCache _cache;
    private readonly IReadOnlyList<EntrySnapshot> _entries;

    internal QueryCacheSnapshot(QueryCache cache, IReadOnlyList<EntrySnapshot> entries, DateTimeOffset createdAt)
    {
        _cache = cache;
        _entries = entries;
        CreatedAt = createdAt;
        Keys = entries.Select(e => e.Key).ToArray();
    }

    /// <summary>Gets when the snapshot was taken.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Gets the keys of the queries in the snapshot.</summary>
    public IReadOnlyList<QueryKey> Keys { get; }

    /// <summary>
    /// Restores every query in the snapshot to its captured state, cancelling (and detaching) any fetch in flight for
    /// it so an outdated result cannot land on top. Observers are notified.
    /// </summary>
    /// <param name="removeOthers">
    /// Also remove queries that were not in the snapshot (observed ones are reset instead, see
    /// <see cref="QueryCache.Remove(QueryKey)"/>), so the cache matches the snapshot exactly.
    /// </param>
    /// <returns>The number of queries restored.</returns>
    public int Restore(bool removeOthers = false)
    {
        var restored = 0;
        foreach (var entry in _entries)
        {
            if (entry.Restore(_cache))
            {
                restored++;
            }
        }

        if (removeOthers)
        {
            var keep = new HashSet<QueryKey>(Keys);
            foreach (var key in _cache.Keys)
            {
                if (!keep.Contains(key))
                {
                    _cache.Remove(key);
                }
            }
        }

        return restored;
    }
}

/// <summary>One entry's captured state; typed so it can be restored without reflection.</summary>
internal abstract class EntrySnapshot(QueryKey key)
{
    public QueryKey Key { get; } = key;

    public abstract bool Restore(QueryCache cache);
}

internal sealed class EntrySnapshot<T>(QueryKey key, QueryState<T> state, bool invalidated) : EntrySnapshot(key)
{
    public override bool Restore(QueryCache cache)
    {
        try
        {
            while (true)
            {
                if (cache.GetOrCreate<T>(Key).TryRestoreSnapshot(state, invalidated) is { } restored)
                {
                    return restored;
                }
            }
        }
        catch (InvalidOperationException)
        {
            return false; // the key is now used with a different data type
        }
    }
}
