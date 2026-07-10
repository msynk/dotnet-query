namespace DotNetQuery;

/// <summary>
/// The public-facing result of a query: an immutable view over the query's current
/// <see cref="QueryState{T}"/> together with its <see cref="QueryKey"/>.
/// </summary>
/// <typeparam name="T">The type of data produced by the query.</typeparam>
public readonly record struct QueryResult<T>
{
    private readonly QueryState<T> _state;

    internal QueryResult(QueryKey key, QueryState<T> state)
    {
        Key = key;
        _state = state;
    }

    /// <summary>Gets the key identifying this query.</summary>
    public QueryKey Key { get; }

    /// <summary>Gets the full immutable state snapshot backing this result.</summary>
    public QueryState<T> State => _state;

    /// <summary>Gets the fetched data, if any.</summary>
    public T? Data => _state.Data;

    /// <summary>Gets the error from the most recent failed fetch, if any.</summary>
    public Exception? Error => _state.Error;

    /// <summary>Gets the lifecycle status of the query.</summary>
    public QueryStatus Status => _state.Status;

    /// <summary>Gets a value indicating whether the query is loading for the first time.</summary>
    public bool IsLoading => _state.IsLoading;

    /// <summary>
    /// Gets a value indicating whether the query has neither data nor an error yet (idle or first load).
    /// The stable condition for a first-load spinner.
    /// </summary>
    public bool IsPending => _state.IsPending;

    /// <summary>Gets a value indicating whether a fetch is currently in flight.</summary>
    public bool IsFetching => _state.IsFetching;

    /// <summary>Gets a value indicating whether the query has data from a successful fetch.</summary>
    public bool IsSuccess => _state.IsSuccess;

    /// <summary>Gets a value indicating whether the most recent fetch failed.</summary>
    public bool IsError => _state.IsError;

    /// <summary>Gets a value indicating whether the query currently holds data.</summary>
    public bool HasData => _state.HasData;

    /// <summary>
    /// Returns the data if present; otherwise throws the captured error, or
    /// <see cref="InvalidOperationException"/> when there is no data and no error.
    /// </summary>
    public T EnsureData()
    {
        if (_state.HasData)
        {
            return _state.Data!;
        }

        if (_state.Error is not null)
        {
            throw _state.Error;
        }

        throw new InvalidOperationException($"Query {Key} has no data (status: {_state.Status}).");
    }
}
