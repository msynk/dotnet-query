namespace DotNetQuery;

/// <summary>
/// An immutable snapshot of a query's state at a point in time.
/// </summary>
/// <typeparam name="T">The type of data produced by the query.</typeparam>
public sealed record QueryState<T>
{
    /// <summary>Gets the lifecycle status of the query.</summary>
    public required QueryStatus Status { get; init; }

    /// <summary>Gets the most recent successfully fetched data, if any.</summary>
    public T? Data { get; init; }

    /// <summary>Gets the error from the most recent failed fetch, if any.</summary>
    public Exception? Error { get; init; }

    /// <summary>Gets the UTC timestamp at which <see cref="Data"/> was last updated.</summary>
    public DateTimeOffset? DataUpdatedAt { get; init; }

    /// <summary>Gets the UTC timestamp at which <see cref="Error"/> was last set.</summary>
    public DateTimeOffset? ErrorUpdatedAt { get; init; }

    /// <summary>Gets the number of consecutive failed fetch attempts for the current fetch cycle.</summary>
    public int FailureCount { get; init; }

    /// <summary>Gets a value indicating whether a fetch is currently in flight.</summary>
    public bool IsFetching => Status is QueryStatus.Loading or QueryStatus.Refetching;

    /// <summary>Gets a value indicating whether the query is loading for the first time (no data yet).</summary>
    public bool IsLoading => Status is QueryStatus.Loading;

    /// <summary>
    /// Gets a value indicating whether the query has neither data nor an error yet — i.e. it is idle or
    /// loading for the first time. Use this for a first-load spinner: it is stable across the brief
    /// <see cref="QueryStatus.Idle"/> → <see cref="QueryStatus.Loading"/> transition.
    /// </summary>
    public bool IsPending => Status is QueryStatus.Idle or QueryStatus.Loading;

    /// <summary>Gets a value indicating whether the query has data from a successful fetch.</summary>
    public bool IsSuccess => Status is QueryStatus.Success or QueryStatus.Refetching && Data is not null;

    /// <summary>Gets a value indicating whether the most recent fetch failed.</summary>
    public bool IsError => Status is QueryStatus.Error;

    /// <summary>Gets a value indicating whether the query is paused.</summary>
    public bool IsPaused => Status is QueryStatus.Paused;

    /// <summary>Gets a value indicating whether the query currently holds usable data.</summary>
    public bool HasData => DataUpdatedAt is not null;

    /// <summary>The initial idle state for a query that has not yet run.</summary>
    public static QueryState<T> Idle { get; } = new() { Status = QueryStatus.Idle };
}
