namespace DotNetQuery;

/// <summary>
/// Non-generic view of a cache entry used by the cache, client and garbage collector,
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

    /// <summary>Whether the entry has been removed from (or collected by) the cache.</summary>
    bool IsRemoved { get; }

    /// <summary>The configured retention window for the entry once it becomes inactive.</summary>
    TimeSpan CacheTime { get; }

    /// <summary>UTC time the entry was last observed, fetched or written, used for GC eligibility.</summary>
    DateTimeOffset LastActivityAt { get; }

    /// <summary>Marks the entry invalidated; returns whether it should be refetched now (active and enabled).</summary>
    bool Invalidate();

    /// <summary>
    /// Refetches using the most recently supplied fetcher and options. Never throws for fetch failures.
    /// Returns a completed task if the entry has never been fetched.
    /// </summary>
    Task RefetchAsync(FetchMode mode, bool includeDisabled, CancellationToken cancellationToken);

    /// <summary>
    /// Refetches only if the entry opts in to <paramref name="trigger"/> and is active and stale, or (for
    /// reconnect) if it has a fetch that was paused while offline. No-op otherwise.
    /// </summary>
    Task RefetchForTriggerAsync(RefetchTrigger trigger, CancellationToken cancellationToken);

    /// <summary>Cancels the in-flight fetch, if any, restoring the previous state.</summary>
    Task CancelAsync();

    /// <summary>Atomically marks the entry removed when it is inactive, idle and past its retention window.</summary>
    bool TryCollect(DateTimeOffset now);

    /// <summary>Produces a non-generic, point-in-time description of the entry for inspection tooling.</summary>
    QueryDescriptor Describe();
}
