namespace DotNetQuery;

/// <summary>
/// Non-generic view of a cache entry used by the cache and garbage collector,
/// which do not know the entry's data type.
/// </summary>
internal interface IQueryEntry : IDisposable
{
    /// <summary>The key identifying the query.</summary>
    QueryKey Key { get; }

    /// <summary>Number of live observers currently subscribed to the entry.</summary>
    int ObserverCount { get; }

    /// <summary>Whether the entry currently has at least one observer.</summary>
    bool IsActive { get; }

    /// <summary>The configured retention window for the entry once it becomes inactive.</summary>
    TimeSpan CacheTime { get; }

    /// <summary>UTC time the entry was last observed or fetched, used for GC eligibility.</summary>
    DateTimeOffset LastActivityAt { get; }

    /// <summary>Marks the entry's data as stale so the next fetch bypasses the freshness check.</summary>
    void MarkStale();

    /// <summary>
    /// Refetches using the most recently supplied fetcher and options, forcing a network call.
    /// Returns a completed task if the entry has never been fetched.
    /// </summary>
    Task RefetchAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Refetches only if the entry opts in to <paramref name="trigger"/> and its data is currently
    /// stale. Used by focus/reconnect/resume handling. No-op otherwise.
    /// </summary>
    Task RefetchForTriggerAsync(RefetchTrigger trigger, CancellationToken cancellationToken);

    /// <summary>Produces a non-generic, point-in-time description of the entry for inspection tooling.</summary>
    QueryDescriptor Describe();
}
