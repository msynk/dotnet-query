using System.Runtime.CompilerServices;

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
public sealed class QueryObserver<T> : IDisposable, IQuerySubscriber<T>
{
    private readonly object _sync = new();
    private readonly Action<QueryObserver<T>> _unsubscribe;
    private readonly Action<Exception>? _onHandlerError;
    private Action<QueryState<T>>? _onChanged;
    private QueryState<T> _current = QueryState<T>.Idle;
    private long _version = -1;
    private int _disposed;
    private StrongBox<T>? _placeholder;

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

    /// <summary>
    /// Gets the most recent state as a <see cref="QueryResult{T}"/> - including the definition's
    /// <see cref="QueryDefinition{T}.PlaceholderData"/> (with <see cref="QueryResult{T}.IsPlaceholderData"/> set)
    /// while the query is pending, when the observer was created from a definition that has one.
    /// </summary>
    public QueryResult<T> CurrentResult => ToResult(CurrentState);

    /// <summary>Wraps <paramref name="state"/> as a result, applying the placeholder while it is pending.</summary>
    internal QueryResult<T> ToResult(QueryState<T> state)
    {
        var result = new QueryResult<T>(Key, state);
        return result.IsPending && TryGetPlaceholder(out var placeholder) ? result.WithPlaceholderData(placeholder) : result;
    }

    /// <summary>Produces placeholder data for <see cref="CurrentResult"/> while the query is pending.</summary>
    internal Func<T>? Placeholder { get; set; }

    // The placeholder is produced once, so repeated reads return the same instance; a throwing factory means
    // "no placeholder" rather than a throwing property getter.
    private bool TryGetPlaceholder(out T placeholder)
    {
        if (_placeholder is { } cached)
        {
            placeholder = cached.Value!;
            return true;
        }

        if (Placeholder is { } factory)
        {
            try
            {
                var produced = new StrongBox<T>(factory());
                _placeholder = produced;
                placeholder = produced.Value!;
                return true;
            }
            catch (Exception)
            {
                Placeholder = null;
            }
        }

        placeholder = default!;
        return false;
    }

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

    void IQuerySubscriber<T>.Seed(QueryState<T> state, long version) => Seed(state, version);

    void IQuerySubscriber<T>.NotifyStateChanged(QueryState<T> state, long version) => NotifyStateChanged(state, version);

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

/// <summary>
/// Receives a cache entry's versioned state changes. <see cref="Seed"/> runs under the entry lock (it must not
/// run user code); <see cref="NotifyStateChanged"/> runs outside it, one notification at a time, in order.
/// </summary>
internal interface IQuerySubscriber<T>
{
    void Seed(QueryState<T> state, long version);

    void NotifyStateChanged(QueryState<T> state, long version);
}
