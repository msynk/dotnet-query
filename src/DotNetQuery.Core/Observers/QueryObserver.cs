namespace DotNetQuery;

/// <summary>
/// A live subscription to a single query. Receives an immediate snapshot on subscription and a
/// new snapshot on every subsequent state change, until disposed.
/// </summary>
/// <typeparam name="T">The type of data produced by the observed query.</typeparam>
public sealed class QueryObserver<T> : IDisposable
{
    private readonly Action<QueryObserver<T>> _unsubscribe;
    private Action<QueryState<T>>? _onChanged;
    private QueryState<T> _current = QueryState<T>.Idle;
    private int _disposed;

    internal QueryObserver(QueryKey key, Action<QueryObserver<T>> unsubscribe)
    {
        Key = key;
        _unsubscribe = unsubscribe;
    }

    /// <summary>Raised whenever the observed query's state changes.</summary>
    public event Action<QueryState<T>>? StateChanged
    {
        add => _onChanged += value;
        remove => _onChanged -= value;
    }

    /// <summary>Gets the key of the observed query.</summary>
    public QueryKey Key { get; }

    /// <summary>Gets the most recently observed state snapshot.</summary>
    public QueryState<T> CurrentState => Volatile.Read(ref _current);

    /// <summary>Gets the most recent state as a <see cref="QueryResult{T}"/>.</summary>
    public QueryResult<T> CurrentResult => new(Key, CurrentState);

    internal void NotifyStateChanged(QueryState<T> state)
    {
        Volatile.Write(ref _current, state);
        _onChanged?.Invoke(state);
    }

    /// <summary>Unsubscribes the observer from the query. Idempotent.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _unsubscribe(this);
        _onChanged = null;
    }
}
