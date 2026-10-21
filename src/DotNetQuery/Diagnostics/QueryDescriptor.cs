namespace DotNetQuery;

/// <summary>
/// A non-generic, point-in-time description of a cache entry, suitable for inspection tooling
/// (DevTools) that does not know the entry's data type.
/// </summary>
public sealed record QueryDescriptor
{
    /// <summary>The key identifying the query.</summary>
    public required QueryKey Key { get; init; }

    /// <summary>The lifecycle status of the query.</summary>
    public required QueryStatus Status { get; init; }

    /// <summary>The name of the query's data type (generic arguments included, e.g. <c>List&lt;Todo&gt;</c>).</summary>
    public required string DataTypeName { get; init; }

    /// <summary>Whether the entry currently holds fetched data.</summary>
    public required bool HasData { get; init; }

    /// <summary>
    /// The entry's current data (a live reference, not a copy), for inspection tooling. Treat it as
    /// read-only. <see langword="null"/> when the entry has no data.
    /// </summary>
    public object? Data { get; init; }

    /// <summary>Whether the entry's data is considered stale and would refetch on next access.</summary>
    public required bool IsStale { get; init; }

    /// <summary>The number of live observers subscribed to the entry.</summary>
    public required int ObserverCount { get; init; }

    /// <summary>Whether the entry has at least one observer.</summary>
    public required bool IsActive { get; init; }

    /// <summary>The number of failed attempts in the current fetch cycle.</summary>
    public required int FailureCount { get; init; }

    /// <summary>The total number of fetch cycles the entry has run.</summary>
    public required int FetchCount { get; init; }

    /// <summary>The message of the most recent error, if any.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>When the data was last updated.</summary>
    public DateTimeOffset? DataUpdatedAt { get; init; }

    /// <summary>When the last error occurred.</summary>
    public DateTimeOffset? ErrorUpdatedAt { get; init; }

    /// <summary>When the entry was last observed, fetched, or written.</summary>
    public required DateTimeOffset LastActivityAt { get; init; }

    /// <summary>The retention window applied once the entry becomes inactive.</summary>
    public required TimeSpan CacheTime { get; init; }

    /// <summary>The freshness window of the most recent fetch, or <see langword="null"/> if never fetched.</summary>
    public TimeSpan? StaleTime { get; init; }
}
