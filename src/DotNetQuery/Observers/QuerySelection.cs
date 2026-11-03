namespace DotNetQuery;

/// <summary>
/// The parts of a query's state an observer can be notified about. Combine flags to choose which changes
/// raise <see cref="QueryObserver{T}.StateChanged"/>; the observer's <see cref="QueryObserver{T}.CurrentState"/>
/// always reflects the latest state either way.
/// </summary>
[Flags]
public enum QueryStateProperties
{
    /// <summary>Never raise notifications (poll <see cref="QueryObserver{T}.CurrentState"/> instead).</summary>
    None = 0,

    /// <summary>The <see cref="QueryState{T}.Status"/> changed (which also covers fetching, pending and paused).</summary>
    Status = 1,

    /// <summary>The (selected) data changed by value.</summary>
    Data = 2,

    /// <summary>The <see cref="QueryState{T}.Error"/> changed.</summary>
    Error = 4,

    /// <summary>The <see cref="QueryState{T}.FailureCount"/> changed (each retry).</summary>
    FailureCount = 8,

    /// <summary>The <see cref="QueryState{T}.DataUpdatedAt"/> timestamp changed (every successful fetch or write).</summary>
    DataUpdatedAt = 16,

    /// <summary>The <see cref="QueryState{T}.ErrorUpdatedAt"/> timestamp changed.</summary>
    ErrorUpdatedAt = 32,

    /// <summary>Every change (the behavior of a plain <c>Watch</c>).</summary>
    All = Status | Data | Error | FailureCount | DataUpdatedAt | ErrorUpdatedAt,
}

/// <summary>
/// Creates observers that project a query's data through a selector and/or notify only about the state
/// properties a consumer actually renders - fewer notifications, fewer UI re-renders.
/// </summary>
public static class QuerySelectionExtensions
{
    /// <summary>
    /// Observes the query under <paramref name="key"/> through <paramref name="select"/>: the observer's data is
    /// <paramref name="select"/>(cached data), recomputed only when the cached data instance changes, and
    /// <see cref="QueryObserver{T}.StateChanged"/> is raised only when one of the <paramref name="notifyOn"/>
    /// properties changes - <see cref="QueryStateProperties.Data"/> meaning the selected value changed by
    /// <paramref name="comparer"/> (default: structural equality, so a selected list that is element-wise equal
    /// does not notify). A selector that throws puts the observer in <see cref="QueryStatus.Error"/> with that
    /// exception while keeping the last good selection.
    /// </summary>
    /// <example>
    /// <code>
    /// // Re-render a badge only when the number of open todos changes, not on every background refetch.
    /// using var openCount = client.Watch(TodoQueries.All, (List&lt;Todo&gt; todos) => todos.Count(t => !t.Done),
    ///     QueryStateProperties.Data | QueryStateProperties.Error);
    /// </code>
    /// </example>
    public static QueryObserver<TResult> Watch<T, TResult>(
        this IQueryClient client,
        QueryKey key,
        Func<T, TResult> select,
        QueryStateProperties notifyOn = QueryStateProperties.All,
        IEqualityComparer<TResult>? comparer = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(select);
        return Subscribe(client, key, select, notifyOn, comparer ?? StructuralEqualityComparer<TResult>.Instance);
    }

    /// <summary>Observes a query definition through <paramref name="select"/> (see <see cref="Watch{T, TResult}(IQueryClient, QueryKey, Func{T, TResult}, QueryStateProperties, IEqualityComparer{TResult}?)"/>).</summary>
    public static QueryObserver<TResult> Watch<T, TResult>(
        this IQueryClient client,
        QueryDefinition<T> query,
        Func<T, TResult> select,
        QueryStateProperties notifyOn = QueryStateProperties.All,
        IEqualityComparer<TResult>? comparer = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(select);
        client.SeedInitialData(query);
        var observer = client.Watch(query.Key, select, notifyOn, comparer);
        if (query.PlaceholderData is { } placeholder)
        {
            observer.Placeholder = () => select(placeholder());
        }

        return observer;
    }

    /// <summary>
    /// Observes the query under <paramref name="key"/>, raising <see cref="QueryObserver{T}.StateChanged"/> only
    /// when one of the <paramref name="notifyOn"/> properties changes. <see cref="QueryStateProperties.Data"/>
    /// fires when the cached data changes by structural equality, so a refetch that returns equal data does not
    /// notify.
    /// </summary>
    public static QueryObserver<T> Watch<T>(this IQueryClient client, QueryKey key, QueryStateProperties notifyOn)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(key);
        return notifyOn is QueryStateProperties.All
            ? client.Watch<T>(key)
            : Subscribe(client, key, static (T data) => data, notifyOn, StructuralEqualityComparer<T>.Instance);
    }

    /// <summary>Observes a query definition, notifying only about <paramref name="notifyOn"/> (see <see cref="Watch{T}(IQueryClient, QueryKey, QueryStateProperties)"/>).</summary>
    public static QueryObserver<T> Watch<T>(this IQueryClient client, QueryDefinition<T> query, QueryStateProperties notifyOn)
    {
        ArgumentNullException.ThrowIfNull(query);
        client.SeedInitialData(query);
        var observer = client.Watch<T>(query.Key, notifyOn);
        observer.Placeholder = query.PlaceholderData;
        return observer;
    }

    private static QueryObserver<TResult> Subscribe<T, TResult>(
        IQueryClient client,
        QueryKey key,
        Func<T, TResult> select,
        QueryStateProperties notifyOn,
        IEqualityComparer<TResult> comparer)
    {
        var sink = client.Cache.Runtime.EventSink;
        while (true)
        {
            var entry = client.Cache.GetOrCreate<T>(key);
            QuerySelection<T, TResult>? selection = null;
            var observer = new QueryObserver<TResult>(
                key,
                _ => entry.RemoveObserver(selection!),
                error => sink?.ObserverFailed(key, error));
            selection = new QuerySelection<T, TResult>(observer, select, notifyOn, comparer);

            if (entry.TryAddObserver(selection))
            {
                // The seed was recorded under the entry lock without running the selector; project it now.
                selection.Activate();
                return observer;
            }
        }
    }
}

/// <summary>
/// Subscribes to an entry of <typeparamref name="T"/> and feeds a projected, filtered
/// <see cref="QueryObserver{T}"/> of <typeparamref name="TResult"/>. Entry notifications already arrive one at a
/// time and in order; the gate additionally orders them against <see cref="Activate"/>, and the entry version
/// discards anything older than what was already applied.
/// </summary>
internal sealed class QuerySelection<T, TResult>(
    QueryObserver<TResult> target,
    Func<T, TResult> select,
    QueryStateProperties notifyOn,
    IEqualityComparer<TResult> comparer) : IQuerySubscriber<T>
{
    private readonly object _gate = new();
    private QueryState<T>? _seed;
    private long _seedVersion = -1;
    private long _applied = -1;
    private QueryState<TResult>? _published;

    // Memoized selection: re-run the selector only when the source data instance changes.
    private bool _hasSource;
    private T? _source;
    private bool _hasSelection;
    private TResult? _selection;
    private Exception? _selectError;

    public void Seed(QueryState<T> state, long version)
    {
        // Called under the entry lock: record only.
        lock (_gate)
        {
            if (version > _seedVersion)
            {
                _seed = state;
                _seedVersion = version;
            }
        }
    }

    public void Activate()
    {
        lock (_gate)
        {
            if (_seed is { } seed)
            {
                Apply(seed, _seedVersion, notify: false);
                _seed = null;
            }
        }
    }

    public void NotifyStateChanged(QueryState<T> state, long version)
    {
        lock (_gate)
        {
            Apply(state, version, notify: true);
        }
    }

    // Must hold _gate.
    private void Apply(QueryState<T> state, long version, bool notify)
    {
        if (version <= _applied)
        {
            return;
        }

        _applied = version;
        var projected = Project(state);
        var changed = _published is null || Differs(_published, projected);
        _published = projected;

        if (notify && changed)
        {
            target.NotifyStateChanged(projected, version);
        }
        else
        {
            target.Seed(projected, version);
        }
    }

    private QueryState<TResult> Project(QueryState<T> state)
    {
        if (state.HasData)
        {
            // A value type is compared by value: boxed for ReferenceEquals, it would never match.
            var sourceChanged = !_hasSource || (typeof(T).IsValueType ? !SafeSourceEquals(_source, state.Data) : !ReferenceEquals(_source, state.Data));
            if (sourceChanged)
            {
                _hasSource = true;
                _source = state.Data;
                try
                {
                    _selection = select(state.Data!);
                    _hasSelection = true;
                    _selectError = null;
                }
                catch (Exception ex)
                {
                    _selectError = ex; // keep the last good selection
                }
            }
        }
        else
        {
            _hasSource = _hasSelection = false;
            _source = default;
            _selection = default;
            _selectError = null;
        }

        return new QueryState<TResult>
        {
            Status = _selectError is not null ? QueryStatus.Error : state.Status,
            Data = _hasSelection ? _selection : default,
            Error = _selectError ?? state.Error,
            DataUpdatedAt = _hasSelection ? state.DataUpdatedAt : null,
            ErrorUpdatedAt = _selectError is not null ? state.DataUpdatedAt : state.ErrorUpdatedAt,
            FailureCount = state.FailureCount,
        };
    }

    private bool Differs(QueryState<TResult> before, QueryState<TResult> after) =>
        (notifyOn.HasFlag(QueryStateProperties.Status) && before.Status != after.Status)
        || (notifyOn.HasFlag(QueryStateProperties.Data) && (before.HasData != after.HasData || !SafeEquals(before.Data, after.Data)))
        || (notifyOn.HasFlag(QueryStateProperties.Error) && !ReferenceEquals(before.Error, after.Error))
        || (notifyOn.HasFlag(QueryStateProperties.FailureCount) && before.FailureCount != after.FailureCount)
        || (notifyOn.HasFlag(QueryStateProperties.DataUpdatedAt) && before.DataUpdatedAt != after.DataUpdatedAt)
        || (notifyOn.HasFlag(QueryStateProperties.ErrorUpdatedAt) && before.ErrorUpdatedAt != after.ErrorUpdatedAt);

    // A value type's Equals is user code: a throwing one means "changed" (re-select) rather than a broken drain.
    private static bool SafeSourceEquals(T? x, T? y)
    {
        try
        {
            return EqualityComparer<T>.Default.Equals(x, y);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // A throwing comparer (user code) must not break the entry's notification drain: treat it as "changed".
    private bool SafeEquals(TResult? x, TResult? y)
    {
        try
        {
            return comparer.Equals(x!, y!);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
