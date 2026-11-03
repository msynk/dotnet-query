namespace DotNetQuery;

/// <summary>
/// <see cref="IQueryClient"/> operations expressed in terms of a <see cref="QueryDefinition{T}"/>, so the
/// key, data type and fetcher always travel together.
/// </summary>
public static class QueryDefinitionExtensions
{
    /// <summary>Fetches the query, serving fresh cached data and deduplicating concurrent requests.</summary>
    public static Task<QueryResult<T>> FetchAsync<T>(
        this IQueryClient client,
        QueryDefinition<T> query,
        bool forceRefetch = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(query);
        client.SeedInitialData(query);
        return client.FetchAsync(query.Key, query.Fetcher, query.Options, forceRefetch, cancellationToken);
    }

    /// <summary>
    /// Fetches a dynamic set of queries in parallel (each deduplicated and cached as usual) and returns their results in
    /// the same order - the counterpart of TanStack's <c>useQueries</c>. For a fixed set of different types, await
    /// several <c>FetchAsync</c> calls with <see cref="Task.WhenAll(Task[])"/> instead.
    /// </summary>
    public static async Task<IReadOnlyList<QueryResult<T>>> FetchAllAsync<T>(
        this IQueryClient client,
        IEnumerable<QueryDefinition<T>> queries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(queries);
        var fetches = queries.Select(query => client.FetchAsync(query, cancellationToken: cancellationToken)).ToArray();
        return await Task.WhenAll(fetches).ConfigureAwait(false);
    }

    /// <summary>Fetches the query into the cache ahead of time, ignoring errors (best-effort warm-up).</summary>
    public static Task PrefetchAsync<T>(this IQueryClient client, QueryDefinition<T> query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(query);
        client.SeedInitialData(query);
        return client.PrefetchAsync(query.Key, query.Fetcher, query.Options, cancellationToken);
    }

    /// <summary>
    /// Returns the cached data when present (fresh or stale), otherwise fetches it. Throws the fetch error if
    /// the query fails - convenient for loaders and server-side prerendering that need the data itself.
    /// </summary>
    public static async Task<T> EnsureQueryDataAsync<T>(this IQueryClient client, QueryDefinition<T> query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(query);
        client.SeedInitialData(query);
        var cached = client.GetQueryData<T>(query.Key);
        if (cached.HasData)
        {
            return cached.Data!;
        }

        var result = await client.FetchAsync(query.Key, query.Fetcher, query.Options, cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.EnsureData();
    }

    /// <summary>Gets the cached state of the query without fetching.</summary>
    public static QueryResult<T> GetQueryData<T>(this IQueryClient client, QueryDefinition<T> query)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(query);
        return client.GetQueryData<T>(query.Key);
    }

    /// <summary>Sets the cached data of the query, marking it fresh and notifying observers.</summary>
    public static void SetQueryData<T>(this IQueryClient client, QueryDefinition<T> query, T data)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(query);
        client.SetQueryData(query.Key, data);
    }

    /// <summary>Atomically replaces the cached data of the query with <paramref name="updater"/>(current data).</summary>
    public static T SetQueryData<T>(this IQueryClient client, QueryDefinition<T> query, Func<T?, T> updater)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(query);
        return client.SetQueryData(query.Key, updater);
    }

    /// <summary>
    /// Creates a live observer for the query, seeding the definition's <see cref="QueryDefinition{T}.InitialData"/>
    /// and exposing its <see cref="QueryDefinition{T}.PlaceholderData"/> through
    /// <see cref="QueryObserver{T}.CurrentResult"/>. Dispose it to unsubscribe.
    /// </summary>
    public static QueryObserver<T> Watch<T>(this IQueryClient client, QueryDefinition<T> query)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(query);
        client.SeedInitialData(query);
        var observer = client.Watch<T>(query.Key);
        observer.Placeholder = query.PlaceholderData;
        return observer;
    }

    /// <summary>
    /// Seeds the cache with the definition's <see cref="QueryDefinition{T}.InitialData"/> if the query has no data
    /// yet (and remembers it for <c>ResetQueriesAsync</c>). Called automatically by the definition-based fetch,
    /// prefetch, ensure and watch operations; adapters that subscribe by key call it directly.
    /// </summary>
    /// <returns><see langword="true"/> if initial data was written to the cache.</returns>
    public static bool SeedInitialData<T>(this IQueryClient client, QueryDefinition<T> query)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(query);
        if (query.InitialData is not { } factory)
        {
            return false;
        }

        while (true)
        {
            if (client.Cache.GetOrCreate<T>(query.Key).TrySeedInitialData(factory, query.InitialDataUpdatedAt) is { } applied)
            {
                return applied;
            }
        }
    }

    /// <summary>Invalidates the query and every query whose key it prefixes, refetching active ones.</summary>
    public static Task InvalidateQueriesAsync<T>(this IQueryClient client, QueryDefinition<T> query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(query);
        return client.InvalidateQueriesAsync(query.Key, cancellationToken);
    }

    /// <summary>Cancels the in-flight fetch of the query (and of queries its key prefixes).</summary>
    public static Task CancelQueriesAsync<T>(this IQueryClient client, QueryDefinition<T> query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(query);
        return client.CancelQueriesAsync(query.Key, cancellationToken);
    }

    /// <summary>Forces a refetch of the query, cancelling any fetch in flight.</summary>
    public static Task RefetchQueryAsync<T>(this IQueryClient client, QueryDefinition<T> query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(query);
        return client.RefetchQueryAsync(query.Key, cancellationToken);
    }

    /// <summary>
    /// Creates an optimistic update targeting this query, for
    /// <see cref="MutationOptions{TVariables, TData}.OptimisticUpdates"/>.
    /// </summary>
    /// <example><c>OptimisticUpdates = [TodoQueries.List(api).Optimistic&lt;List&lt;Todo&gt;, Todo&gt;((todos, added) => [.. todos ?? [], added])]</c></example>
    public static OptimisticUpdate<TVariables> Optimistic<T, TVariables>(this QueryDefinition<T> query, Func<T?, TVariables, T> update)
    {
        ArgumentNullException.ThrowIfNull(query);
        return OptimisticUpdate.Create(query.Key, update);
    }
}
