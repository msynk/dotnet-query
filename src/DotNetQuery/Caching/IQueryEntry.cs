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

    /// <summary>Whether a fetch is currently in flight.</summary>
    bool IsFetching { get; }

    /// <summary>The configured retention window for the entry once it becomes inactive.</summary>
    TimeSpan CacheTime { get; }

    /// <summary>UTC time the entry was last observed, fetched or written, used for GC eligibility.</summary>
    DateTimeOffset LastActivityAt { get; }

    /// <summary>Marks the entry invalidated; returns whether it should be refetched now (active and enabled).</summary>
    /// <param name="changeSequence">
    /// The logical time (<see cref="QueryRuntime.NextSequence"/>) of the change that prompted the invalidation. A refresh
    /// in flight that started after it already reflects the change: the entry is then not marked stale (that refresh
    /// covers the invalidation). <see cref="long.MaxValue"/> (unknown) never counts as covered.
    /// </param>
    bool Invalidate(long changeSequence = long.MaxValue);

    /// <summary>
    /// Refetches using the most recently supplied fetcher and options. Never throws for fetch failures.
    /// Returns a completed task if the entry has never been fetched.
    /// </summary>
    /// <param name="mode">How to treat freshness and an in-flight fetch.</param>
    /// <param name="includeDisabled">Whether to refetch a disabled query.</param>
    /// <param name="cancellationToken">Cancels this caller's wait only.</param>
    /// <param name="joinRefreshStartedAfter">
    /// With <see cref="FetchMode.CancelAndRefetch"/>: join (instead of cancelling) a refresh in flight that started after
    /// this logical time, because it already reflects the change being reconciled.
    /// </param>
    Task RefetchAsync(FetchMode mode, bool includeDisabled, CancellationToken cancellationToken, long joinRefreshStartedAfter = long.MaxValue);

    /// <summary>
    /// Refetches only if the entry opts in to <paramref name="trigger"/> and is active and stale, or (for
    /// reconnect) if it has a fetch that was paused while offline. No-op otherwise.
    /// </summary>
    Task RefetchForTriggerAsync(RefetchTrigger trigger, CancellationToken cancellationToken);

    /// <summary>Cancels the in-flight fetch, if any, restoring the previous state.</summary>
    Task CancelAsync();

    /// <summary>
    /// Resets the entry to its initial state (no data, or its initial data), cancelling and detaching any in-flight
    /// fetch. Completes once the reset has been applied.
    /// </summary>
    Task ResetAsync();

    /// <summary>
    /// Resets like <see cref="ResetAsync"/> but defers cancelling the detached fetch to the returned action, so the
    /// caller can start a follow-up refetch first (which then inherits the detached fetch's waiters).
    /// </summary>
    Action ResetAndDetach();

    /// <summary>Marks the entry removed if nothing observes it; returns <see langword="false"/> (doing nothing) otherwise.</summary>
    bool TryRemoveUnobserved();

    /// <summary>Returns whether the entry currently satisfies <paramref name="filter"/>. Runs the filter's predicate outside the entry lock.</summary>
    bool Matches(QueryFilter filter);

    /// <summary>Atomically marks the entry removed when it is inactive and idle, regardless of its retention window.</summary>
    bool TryEvict();

    /// <summary>Captures the entry's state for a <see cref="QueryCacheSnapshot"/>, or <see langword="null"/> if removed.</summary>
    EntrySnapshot? CaptureSnapshot();

    /// <summary>Atomically marks the entry removed when it is inactive, idle and past its retention window.</summary>
    bool TryCollect(DateTimeOffset now);

    /// <summary>Produces a non-generic, point-in-time description of the entry for inspection tooling.</summary>
    QueryDescriptor Describe();
}
