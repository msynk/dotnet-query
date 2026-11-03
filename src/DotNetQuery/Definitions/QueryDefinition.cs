namespace DotNetQuery;

/// <summary>
/// A reusable, strongly-typed description of a query: its <see cref="Key"/>, the <see cref="Fetcher"/> that
/// loads it, and optional <see cref="Options"/>. Define each query once and use the definition everywhere -
/// fetching, prefetching, reading, writing, observing, invalidating, and in UI components - so a key is
/// always paired with the same data type and fetcher.
/// </summary>
/// <typeparam name="T">The type of data the query produces.</typeparam>
/// <example>
/// <code>
/// public static class TodoQueries
/// {
///     public static readonly QueryKey All = QueryKey.Create("todos");
///
///     public static QueryDefinition&lt;List&lt;Todo&gt;&gt; List(TodoApi api) =>
///         new(All, ct => api.GetAllAsync(ct));
///
///     public static QueryDefinition&lt;Todo&gt; ById(TodoApi api, int id) =>
///         new(All.Append(id), ct => api.GetAsync(id, ct), new QueryOptions { StaleTime = TimeSpan.FromMinutes(1) });
/// }
///
/// var todos = await client.FetchAsync(TodoQueries.List(api));
/// client.SetQueryData(TodoQueries.ById(api, 7), updatedTodo);   // type-checked against Todo
/// await client.InvalidateQueriesAsync(TodoQueries.All);          // refreshes the list and every todo
/// </code>
/// </example>
public sealed record QueryDefinition<T>
{
    /// <summary>Creates a query definition.</summary>
    /// <param name="key">The key identifying the query.</param>
    /// <param name="fetcher">Delegate that loads the data, honoring the cancellation token.</param>
    /// <param name="options">Optional per-query options.</param>
    public QueryDefinition(QueryKey key, Func<CancellationToken, Task<T>> fetcher, QueryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(fetcher);
        Key = key;
        Fetcher = fetcher;
        Options = options;
    }

    /// <summary>Gets the key identifying the query.</summary>
    public QueryKey Key { get; init; }

    /// <summary>Gets the delegate that loads the data.</summary>
    public Func<CancellationToken, Task<T>> Fetcher { get; init; }

    /// <summary>Gets the per-query options, or <see langword="null"/> for the client defaults.</summary>
    public QueryOptions? Options { get; init; }

    /// <summary>
    /// Produces data to seed the cache with when the query has no data yet (it has never succeeded and is not in
    /// an error state) - for example a detail record taken from an already cached list. Initial data is real
    /// cached data: it is shared, persisted and inspected like fetched data, and counts as fresh from
    /// <see cref="InitialDataUpdatedAt"/> for the query's stale time, so a fresh seed is served without a
    /// request while a stale one is shown immediately and refetched in the background. Applied when the
    /// definition is fetched, prefetched, ensured or watched, and restored by <c>ResetQueriesAsync</c>.
    /// </summary>
    public Func<T>? InitialData { get; init; }

    /// <summary>
    /// When <see cref="InitialData"/> was produced, used to decide whether it is stale (for example the source
    /// list's <c>DataUpdatedAt</c>). <see langword="null"/> means "now": the seed is fresh for the stale time.
    /// </summary>
    public DateTimeOffset? InitialDataUpdatedAt { get; init; }

    /// <summary>
    /// Produces data to show while the query is pending its first result. Unlike <see cref="InitialData"/> it is
    /// never written to the cache: observers created from this definition report it through
    /// <see cref="QueryObserver{T}.CurrentResult"/> with <see cref="QueryResult{T}.IsPlaceholderData"/> set, and
    /// it disappears as soon as real data (or an error) arrives.
    /// </summary>
    public Func<T>? PlaceholderData { get; init; }

    /// <summary>Returns a copy of this definition with <paramref name="options"/> applied.</summary>
    public QueryDefinition<T> WithOptions(QueryOptions? options) => this with { Options = options };

    /// <summary>Returns a copy of this definition that seeds the cache with <paramref name="data"/> (see <see cref="InitialData"/>).</summary>
    /// <param name="data">The initial data.</param>
    /// <param name="updatedAt">When the data was produced; <see langword="null"/> means now.</param>
    public QueryDefinition<T> WithInitialData(T data, DateTimeOffset? updatedAt = null) =>
        this with { InitialData = () => data, InitialDataUpdatedAt = updatedAt };

    /// <summary>Returns a copy of this definition that shows <paramref name="data"/> while pending (see <see cref="PlaceholderData"/>).</summary>
    public QueryDefinition<T> WithPlaceholderData(T data) => this with { PlaceholderData = () => data };

    /// <summary>Returns a copy of this definition with its options transformed by <paramref name="configure"/>.</summary>
    /// <example><c>TodoQueries.ById(api, id).WithOptions(o => o with { Enabled = id &gt; 0 })</c></example>
    public QueryDefinition<T> WithOptions(Func<QueryOptions, QueryOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return this with { Options = configure(Options ?? new QueryOptions()) };
    }
}
