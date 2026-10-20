namespace DotNetQuery;

/// <summary>
/// The central orchestrator of DotNetQuery: owns the query cache and coordinates fetching,
/// deduplication, caching, invalidation and observation of server state.
/// </summary>
/// <remarks>
/// Prefer describing each query once as a <see cref="QueryDefinition{T}"/> (key + fetcher + options) and
/// using the definition-based extension methods in <see cref="QueryDefinitionExtensions"/>, so a key is
/// always paired with the same data type and fetcher.
/// </remarks>
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
    /// the cached data is still fresh, cancelling an in-flight fetch for the key so the result reflects the
    /// latest inputs (callers awaiting the cancelled fetch receive the new result). Useful when the
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

    /// <summary>
    /// Imperatively sets the cached data for a query, marking it fresh and notifying observers. Note that an
    /// in-flight fetch for the key will overwrite it when it completes - call
    /// <see cref="CancelQueriesAsync(QueryKey, CancellationToken)"/> first for optimistic updates.
    /// </summary>
    void SetQueryData<T>(QueryKey key, T data);

    /// <summary>
    /// Atomically replaces the cached data for a query with the result of <paramref name="updater"/>, which
    /// receives the current data (or <see langword="default"/> when there is none). Marks the data fresh and
    /// notifies observers. Returns the new data.
    /// </summary>
    /// <remarks>
    /// The updater runs while the entry is locked: keep it fast and side-effect free, and treat the current
    /// value as immutable (return a new instance rather than mutating it).
    /// </remarks>
    T SetQueryData<T>(QueryKey key, Func<T?, T> updater);

    /// <summary>
    /// Seeds the cache with data produced elsewhere - a server prerender, a persisted cache, a parent
    /// component - keeping its original <paramref name="dataUpdatedAt"/> so staleness is computed correctly.
    /// The data is applied only if the cache has no data for the key or holds older data.
    /// </summary>
    /// <returns><see langword="true"/> if the data was applied.</returns>
    bool HydrateQueryData<T>(QueryKey key, T data, DateTimeOffset dataUpdatedAt);

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
    /// Cancels the in-flight fetch of every query whose key is prefixed by <paramref name="key"/>, restoring
    /// each query's previous state. Await it at the start of an optimistic update so a pending refetch cannot
    /// overwrite the optimistic data. Completes once the cancelled fetches have settled.
    /// </summary>
    Task CancelQueriesAsync(QueryKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels the in-flight fetch of every query whose key matches <paramref name="predicate"/>, restoring
    /// each query's previous state.
    /// </summary>
    Task CancelQueriesAsync(Func<QueryKey, bool> predicate, CancellationToken cancellationToken = default);

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
    /// Creates an observable infinite (paginated) query bound to a page fetcher. Its pages live in the cache
    /// under <paramref name="key"/> as an <see cref="InfiniteData{TParam, TPage}"/>, so they are deduplicated,
    /// invalidated, inspected, persisted and hydrated like any other query, and shared by every infinite
    /// query created for the same key. Dispose the returned query to stop observing it.
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
    /// Forces a refetch of a single query by key using its last-known fetcher, regardless of staleness,
    /// observers, or <see cref="QueryOptions.Enabled"/>, cancelling an in-flight fetch first. Never throws for
    /// fetch failures (they are captured in the query state). No-op if the query has never been fetched.
    /// </summary>
    Task RefetchQueryAsync(QueryKey key, CancellationToken cancellationToken = default);

    /// <summary>Removes a single query from the cache, cancelling any in-flight fetch.</summary>
    bool RemoveQuery(QueryKey key);

    /// <summary>Removes every query from the cache.</summary>
    void Clear();
}
