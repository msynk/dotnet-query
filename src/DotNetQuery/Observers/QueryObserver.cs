namespace DotNetQuery;

/// <summary>
/// A live subscription to a single query. Receives an immediate snapshot on subscription and a
/// new snapshot on every subsequent state change, until disposed.
/// </summary>
/// <remarks>
/// <see cref="StateChanged"/> may be raised on any thread (typically the thread that completed the fetch);
/// UI adapters marshal to their dispatcher. Notifications for a query are delivered one at a time and in
/// order, so handlers observe every transition (for example Loading then Success) exactly once.
/// A handler that throws is isolated - it cannot fail the fetch or starve other handlers - and the error is
/// reported as a <see cref="QueryObserverErrorEvent"/>.
/// </remarks>
/// <typeparam name="T">The type of data produced by the observed query.</typeparam>
public sealed class QueryObserver<T> : IDisposable
{
    private readonly object _sync = new();
    private readonly Action<QueryObserver<T>> _unsubscribe;
    private readonly Action<Exception>? _onHandlerError;
    private Action<QueryState<T>>? _onChanged;
    private QueryState<T> _current = QueryState<T>.Idle;
    private long _version = -1;
    private int _disposed;

    internal QueryObserver(QueryKey key, Action<QueryObserver<T>> unsubscribe, Action<Exception>? onHandlerError = null)
    {
        Key = key;
        _unsubscribe = unsubscribe;
        _onHandlerError = onHandlerError;
    }

    /// <summary>Raised whenever the observed query's state changes.</summary>
    public event Action<QueryState<T>>? StateChanged
    {
        add { lock (_sync) { _onChanged += value; } }
        remove { lock (_sync) { _onChanged -= value; } }
    }

    /// <summary>Gets the key of the observed query.</summary>
    public QueryKey Key { get; }

    /// <summary>Gets the most recently observed state snapshot.</summary>
    public QueryState<T> CurrentState
    {
        get { lock (_sync) { return _current; } }
    }

    /// <summary>Gets the most recent state as a <see cref="QueryResult{T}"/>.</summary>
    public QueryResult<T> CurrentResult => new(Key, CurrentState);

    /// <summary>Gets a value indicating whether the observer has been disposed.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Sets the initial snapshot without raising <see cref="StateChanged"/> (no handlers exist yet).</summary>
    internal void Seed(QueryState<T> state, long version)
    {
        lock (_sync)
        {
            if (version > _version)
            {
                _version = version;
                _current = state;
            }
        }
    }

    /// <summary>Applies a versioned state change; stale (out-of-order) versions are ignored.</summary>
    internal void NotifyStateChanged(QueryState<T> state, long version)
    {
        Action<QueryState<T>>? handlers;
        lock (_sync)
        {
            if (version <= _version)
            {
                return;
            }

            _version = version;
            _current = state;
            handlers = _onChanged;
        }

        if (handlers is null || IsDisposed)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<QueryState<T>>)handler)(state);
            }
            catch (Exception ex)
            {
                _onHandlerError?.Invoke(ex);
            }
        }
    }

    /// <summary>Unsubscribes the observer from the query. Idempotent.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _unsubscribe(this);
        lock (_sync)
        {
            _onChanged = null;
        }
    }
}
