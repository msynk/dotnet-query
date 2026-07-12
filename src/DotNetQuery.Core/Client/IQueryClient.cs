namespace DotNetQuery;

/// <summary>
/// The central orchestrator of DotNetQuery: owns the query cache and coordinates fetching,
/// deduplication, caching, invalidation and observation of server state.
/// </summary>
public interface IQueryClient : IDisposable
{
    /// <summary>Gets the underlying query cache.</summary>
    QueryCache Cache { get; }

    /// <summary>
    /// Raised for every diagnostic event (query/mutation lifecycle, invalidation). Complements the
    /// OpenTelemetry <see cref="DotNetQueryDiagnostics"/> tracing and metrics.
    /// </summary>
    event Action<DotNetQueryEvent>? DiagnosticEvent;

    /// <summary>
    /// Fetches data for <paramref name="key"/>, serving fresh cached data when available and
    /// deduplicating concurrent requests for the same key.
    /// </summary>
    /// <typeparam name="T">The type of data produced by the query.</typeparam>
    /// <param name="key">The key identifying the query.</param>
    /// <param name="fetcher">Delegate that performs the actual fetch, honoring the token.</param>
    /// <param name="options">Optional per-query overrides.</param>
    /// <param name="forceRefetch">
    /// When <see langword="true"/>, ignores staleness and re-invokes <paramref name="fetcher"/> even if
    /// the cached data is still fresh (existing in-flight fetches are still deduplicated). Useful when the
    /// fetcher's captured inputs have changed while the key stayed the same.
    /// </param>
    /// <param name="cancellationToken">Cancels this caller's wait without cancelling shared work.</param>
    Task<QueryResult<T>> FetchAsync<T>(
        QueryKey key,
        Func<CancellationToken, Task<T>> fetcher,
        QueryOptions? options = null,
        bool forceRefetch = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches data for <paramref name="key"/> into the cache without returning it, ignoring errors.
    /// Useful for warming the cache ahead of navigation.
    /// </summary>
    Task PrefetchAsync<T>(
        QueryKey key,
        Func<CancellationToken, Task<T>> fetcher,
        QueryOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Gets the current cached state for a query without triggering a fetch.</summary>
    QueryResult<T> GetQueryData<T>(QueryKey key);

    /// <summary>Imperatively sets the cached data for a query, notifying observers.</summary>
    void SetQueryData<T>(QueryKey key, T data);

    /// <summary>
    /// Creates a live observer for a query. The observer receives an immediate snapshot and every
    /// subsequent state change. Dispose it to unsubscribe.
    /// </summary>
    QueryObserver<T> Watch<T>(QueryKey key);

    /// <summary>
    /// Marks every query whose key is prefixed by <paramref name="key"/> as stale and refetches
    /// those with active observers.
    /// </summary>
    Task InvalidateQueriesAsync(QueryKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks every query whose key matches <paramref name="predicate"/> as stale and refetches
    /// those with active observers.
    /// </summary>
    Task InvalidateQueriesAsync(Func<QueryKey, bool> predicate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a reusable, observable mutation bound to <paramref name="mutationFn"/>. Invoke it via
    /// <see cref="Mutation{TVariables, TData}.MutateAsync"/>.
    /// </summary>
    Mutation<TVariables, TData> CreateMutation<TVariables, TData>(
        Func<TVariables, CancellationToken, Task<TData>> mutationFn,
        MutationOptions<TVariables, TData>? options = null);

    /// <summary>
    /// Runs a one-shot mutation with optimistic updates, rollback, retries and lifecycle callbacks.
    /// </summary>
    Task<MutationResult<TVariables, TData>> MutateAsync<TVariables, TData>(
        Func<TVariables, CancellationToken, Task<TData>> mutationFn,
        TVariables variables,
        MutationOptions<TVariables, TData>? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a reusable, observable infinite (paginated) query bound to a page fetcher. The query is
    /// registered under <paramref name="key"/> so that <see cref="InvalidateQueriesAsync(QueryKey, CancellationToken)"/>
    /// refetches it. Dispose the returned query to unregister it.
    /// </summary>
    /// <typeparam name="TParam">The page-parameter type (offset, cursor, etc.).</typeparam>
    /// <typeparam name="TPage">The page type.</typeparam>
    /// <param name="key">The key identifying the infinite query, for invalidation.</param>
    /// <param name="fetcher">Fetches a single page given its page parameter.</param>
    /// <param name="options">Pagination configuration.</param>
    InfiniteQuery<TParam, TPage> CreateInfiniteQuery<TParam, TPage>(
        QueryKey key,
        Func<TParam, CancellationToken, Task<TPage>> fetcher,
        InfiniteQueryOptions<TParam, TPage> options);

    /// <summary>
    /// Forces a refetch of a single query by key using its last-known fetcher, regardless of staleness
    /// or observers. No-op if the query has never been fetched.
    /// </summary>
    Task RefetchQueryAsync(QueryKey key, CancellationToken cancellationToken = default);

    /// <summary>Removes a single query from the cache, cancelling any in-flight fetch.</summary>
    bool RemoveQuery(QueryKey key);

    /// <summary>Removes every query from the cache.</summary>
    void Clear();
}
