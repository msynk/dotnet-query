namespace DotNetQuery;

/// <summary>
/// Shared, per-cache services handed to every entry: the clock, the diagnostics sink, connectivity, and
/// the cache's change notifications. Keeps <see cref="QueryEntry{T}"/> decoupled from the client.
/// </summary>
internal sealed class QueryRuntime
{
    private readonly QueryCache _cache;

    public QueryRuntime(QueryCache cache, TimeProvider timeProvider, IQueryEventSink? eventSink, IConnectivityProvider? connectivity, TimeSpan defaultCacheTime)
    {
        _cache = cache;
        TimeProvider = timeProvider;
        EventSink = eventSink;
        Connectivity = connectivity;
        DefaultCacheTime = defaultCacheTime;
    }

    public TimeProvider TimeProvider { get; }

    public IQueryEventSink? EventSink { get; }

    public IConnectivityProvider? Connectivity { get; }

    public TimeSpan DefaultCacheTime { get; }

    /// <summary>Whether fetches may hit the network. Always true without a connectivity provider.</summary>
    public bool IsOnline => Connectivity?.IsOnline ?? true;

    public void RaiseChanged(QueryKey key, QueryCacheChangeKind kind) => _cache.RaiseChanged(key, kind);
}

/// <summary>How a fetch request treats staleness and an already in-flight fetch.</summary>
internal enum FetchMode
{
    /// <summary>Serve fresh cached data; otherwise fetch, joining any in-flight fetch.</summary>
    IfStale,

    /// <summary>Fetch even when fresh, but join (deduplicate with) any in-flight fetch.</summary>
    Force,

    /// <summary>
    /// Fetch even when fresh, cancelling any in-flight fetch first so the result reflects the latest
    /// server state (used by invalidation and explicit refetches). Waiters of the cancelled fetch are
    /// handed over to the new one.
    /// </summary>
    CancelAndRefetch,
}
