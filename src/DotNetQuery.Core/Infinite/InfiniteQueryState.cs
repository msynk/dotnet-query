namespace DotNetQuery;

/// <summary>
/// An immutable snapshot of an infinite query's state.
/// </summary>
/// <typeparam name="TParam">The page-parameter type.</typeparam>
/// <typeparam name="TPage">The page type.</typeparam>
public sealed record InfiniteQueryState<TParam, TPage>
{
    /// <summary>Gets the lifecycle status of the query.</summary>
    public required QueryStatus Status { get; init; }

    /// <summary>Gets the accumulated pages and their parameters.</summary>
    public InfiniteData<TParam, TPage> Data { get; init; } = InfiniteData<TParam, TPage>.Empty;

    /// <summary>Gets the error from the most recent failed fetch, if any.</summary>
    public Exception? Error { get; init; }

    /// <summary>Gets a value indicating whether another page exists after the last one.</summary>
    public bool HasNextPage { get; init; }

    /// <summary>Gets a value indicating whether another page exists before the first one.</summary>
    public bool HasPreviousPage { get; init; }

    /// <summary>Gets a value indicating whether the next page is currently being fetched.</summary>
    public bool IsFetchingNextPage { get; init; }

    /// <summary>Gets a value indicating whether the previous page is currently being fetched.</summary>
    public bool IsFetchingPreviousPage { get; init; }

    /// <summary>Gets the UTC timestamp at which the data was last updated.</summary>
    public DateTimeOffset? DataUpdatedAt { get; init; }

    /// <summary>Gets the number of failed attempts in the current fetch cycle.</summary>
    public int FailureCount { get; init; }

    /// <summary>Gets a value indicating whether the query is loading its first page.</summary>
    public bool IsLoading => Status is QueryStatus.Loading;

    /// <summary>Gets a value indicating whether the query has neither pages nor an error yet (idle or first load).</summary>
    public bool IsPending => Status is QueryStatus.Idle or QueryStatus.Loading;

    /// <summary>Gets a value indicating whether any fetch (first page, next/previous page, or refetch) is in flight.</summary>
    public bool IsFetching => Status is QueryStatus.Loading or QueryStatus.Refetching;

    /// <summary>Gets a value indicating whether the query has at least one page.</summary>
    public bool IsSuccess => Status is QueryStatus.Success or QueryStatus.Refetching && Data.Pages.Count > 0;

    /// <summary>Gets a value indicating whether the most recent fetch failed.</summary>
    public bool IsError => Status is QueryStatus.Error;

    /// <summary>The initial idle state.</summary>
    public static InfiniteQueryState<TParam, TPage> Idle { get; } = new() { Status = QueryStatus.Idle };
}
