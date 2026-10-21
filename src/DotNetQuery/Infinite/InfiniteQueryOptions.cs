namespace DotNetQuery;

/// <summary>
/// Callback that, given the most recently fetched page in a direction and the full accumulated
/// pages and parameters, returns the parameter for the adjacent page (or
/// <see cref="NextPageParam{T}.None"/> when there is none).
/// </summary>
/// <typeparam name="TParam">The page-parameter type.</typeparam>
/// <typeparam name="TPage">The page type.</typeparam>
public delegate NextPageParam<TParam> PageParamResolver<TParam, TPage>(
    TPage edgePage,
    IReadOnlyList<TPage> allPages,
    IReadOnlyList<TParam> allPageParams);

/// <summary>
/// Configuration for an infinite (paginated) query.
/// </summary>
/// <typeparam name="TParam">The type of the page parameter (offset, cursor, etc.).</typeparam>
/// <typeparam name="TPage">The type of a single page of data.</typeparam>
public sealed record InfiniteQueryOptions<TParam, TPage>
{
    /// <summary>The parameter used to fetch the first page.</summary>
    public required TParam InitialPageParam { get; init; }

    /// <summary>Resolves the parameter for the next (forward) page from the last page.</summary>
    public required PageParamResolver<TParam, TPage> GetNextPageParam { get; init; }

    /// <summary>Resolves the parameter for the previous (backward) page from the first page.</summary>
    public PageParamResolver<TParam, TPage>? GetPreviousPageParam { get; init; }

    /// <summary>
    /// Optional maximum number of pages to retain. When exceeded, fetching in one direction drops a
    /// page from the opposite end (a sliding window). <see langword="null"/> retains all pages.
    /// </summary>
    public int? MaxPages { get; init; }

    /// <summary>
    /// Maximum number of automatic retries after a failed fetch. Defaults to 0. Ignored when
    /// <see cref="QueryOptions"/> specifies <see cref="DotNetQuery.QueryOptions.RetryCount"/>.
    /// </summary>
    public int RetryCount { get; init; }

    /// <summary>
    /// Strategy computing the delay between retries. Defaults to no delay. Ignored when
    /// <see cref="QueryOptions"/> specifies <see cref="DotNetQuery.QueryOptions.RetryDelay"/>.
    /// </summary>
    public RetryDelayStrategy? RetryDelay { get; init; }

    /// <summary>
    /// Caching and background behavior for the cached pages (stale time, cache time, refetch triggers,
    /// interval, enabled, ...). Unset properties fall back to the client defaults.
    /// </summary>
    public QueryOptions? QueryOptions { get; init; }
}
