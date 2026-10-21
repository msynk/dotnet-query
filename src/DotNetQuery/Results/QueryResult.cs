namespace DotNetQuery;

/// <summary>
/// The public-facing result of a query: an immutable view over the query's current
/// <see cref="QueryState{T}"/> together with its <see cref="QueryKey"/>.
/// </summary>
/// <typeparam name="T">The type of data produced by the query.</typeparam>
public readonly record struct QueryResult<T>
{
    private readonly QueryState<T>? _state;

    /// <summary>Creates a result view over <paramref name="state"/> for <paramref name="key"/> (for adapters and tests).</summary>
    public QueryResult(QueryKey key, QueryState<T> state)
        : this(key, state, isPlaceholderData: false)
    {
    }

    internal QueryResult(QueryKey key, QueryState<T> state, bool isPlaceholderData)
    {
        Key = key;
        _state = state;
        IsPlaceholderData = isPlaceholderData;
    }

    /// <summary>Gets the key identifying this query.</summary>
    public QueryKey Key { get; }

    /// <summary>Gets the full immutable state snapshot backing this result.</summary>
    public QueryState<T> State => _state ?? QueryState<T>.Idle;

    /// <summary>Gets the fetched data, if any.</summary>
    public T? Data => State.Data;

    /// <summary>Gets the error from the most recent failed fetch, if any.</summary>
    public Exception? Error => State.Error;

    /// <summary>Gets the lifecycle status of the query.</summary>
    public QueryStatus Status => State.Status;

    /// <summary>Gets the UTC timestamp at which <see cref="Data"/> was last updated.</summary>
    public DateTimeOffset? DataUpdatedAt => State.DataUpdatedAt;

    /// <summary>Gets the number of failed attempts in the current fetch cycle.</summary>
    public int FailureCount => State.FailureCount;

    /// <summary>Gets a value indicating whether the query is loading for the first time.</summary>
    public bool IsLoading => State.IsLoading;

    /// <summary>
    /// Gets a value indicating whether the query has neither data nor an error yet (idle or first load).
    /// The stable condition for a first-load spinner.
    /// </summary>
    public bool IsPending => State.IsPending;

    /// <summary>Gets a value indicating whether a fetch is currently in flight.</summary>
    public bool IsFetching => State.IsFetching;

    /// <summary>Gets a value indicating whether the query already has data and is refetching it in the background.</summary>
    public bool IsRefetching => State.IsRefetching;

    /// <summary>Gets a value indicating whether the query has data from a successful fetch.</summary>
    public bool IsSuccess => State.IsSuccess;

    /// <summary>Gets a value indicating whether the most recent fetch failed.</summary>
    public bool IsError => State.IsError;

    /// <summary>Gets a value indicating whether the first fetch failed, so there is no data to show.</summary>
    public bool IsLoadingError => State.IsLoadingError;

    /// <summary>Gets a value indicating whether a refetch failed while previous data is still available.</summary>
    public bool IsRefetchError => State.IsRefetchError;

    /// <summary>Gets a value indicating whether the query is paused waiting for connectivity.</summary>
    public bool IsPaused => State.IsPaused;

    /// <summary>Gets a value indicating whether the query currently holds data.</summary>
    public bool HasData => State.HasData;

    /// <summary>
    /// Gets a value indicating whether <see cref="Data"/> is placeholder data carried over from a previous
    /// query (see <see cref="KeepPreviousData"/>) while this query loads its own first result.
    /// </summary>
    public bool IsPlaceholderData { get; }

    /// <summary>
    /// Returns the data if present; otherwise throws the captured error, or
    /// <see cref="InvalidOperationException"/> when there is no data and no error.
    /// </summary>
    public T EnsureData()
    {
        var state = State;
        if (state.HasData)
        {
            return state.Data!;
        }

        if (state.Error is not null)
        {
            throw state.Error;
        }

        throw new InvalidOperationException($"Query {Key} has no data (status: {state.Status}).");
    }

    /// <summary>
    /// Keeps showing <paramref name="previous"/>'s data while this query is still pending its first result
    /// - the building block for flicker-free pagination and filtering, where the key changes but the UI
    /// should keep the last page visible until the next one arrives. The returned result reports
    /// <see cref="QueryStatus.Refetching"/> with <see cref="IsPlaceholderData"/> set. Returns this result
    /// unchanged once it has data or an error, or when <paramref name="previous"/> has no data.
    /// </summary>
    /// <param name="previous">The result previously rendered (typically for the previous key).</param>
    public QueryResult<T> KeepPreviousData(QueryResult<T> previous)
    {
        if (!IsPending || !previous.HasData)
        {
            return this;
        }

        var placeholder = State with
        {
            Status = QueryStatus.Refetching,
            Data = previous.Data,
            DataUpdatedAt = previous.DataUpdatedAt,
        };
        return new QueryResult<T>(Key, placeholder, isPlaceholderData: true);
    }
}
