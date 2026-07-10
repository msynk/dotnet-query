using System.Collections.Concurrent;
using System.Diagnostics;

namespace DotNetQuery;

/// <summary>
/// Default implementation of <see cref="IQueryClient"/>. Generally one instance should exist
/// per application. Thread-safe; all members may be called concurrently.
/// </summary>
public sealed class QueryClient : IQueryClient, IQueryEventSink
{
    private readonly QueryClientOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ITimer _gcTimer;
    private readonly List<(ISignalSource Source, Action Handler)> _signalSubscriptions = [];
    private readonly ConcurrentDictionary<QueryKey, Activity> _activeFetchActivities = new();
    private readonly IConnectivityProvider? _connectivity;
    private readonly Action<bool>? _connectivityHandler;
    private readonly List<WeakReference<IInvalidatableInfiniteQuery>> _infiniteQueries = [];
    private int _disposed;

    /// <inheritdoc />
    public event Action<DotNetQueryEvent>? DiagnosticEvent;

    /// <summary>Initializes a new client.</summary>
    /// <param name="options">Client-wide default options. A fresh default instance is used if <see langword="null"/>.</param>
    /// <param name="timeProvider">Time source for staleness and scheduling. Defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="focusManager">Optional source of focus-regained signals.</param>
    /// <param name="onlineManager">Optional source of connectivity-restored signals.</param>
    /// <param name="resumeManager">Optional source of application-resume signals.</param>
    /// <param name="connectivity">Optional connectivity provider enabling offline pause/resume.</param>
    public QueryClient(
        QueryClientOptions? options = null,
        TimeProvider? timeProvider = null,
        IFocusManager? focusManager = null,
        IOnlineManager? onlineManager = null,
        IResumeManager? resumeManager = null,
        IConnectivityProvider? connectivity = null)
    {
        _options = options ?? new QueryClientOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        Cache = new QueryCache(_timeProvider, this);

        var interval = _options.GarbageCollectionInterval;
        _gcTimer = _timeProvider.CreateTimer(
            static state => ((QueryClient)state!).Cache.CollectGarbage(),
            this,
            interval,
            interval);

        Subscribe(focusManager, RefetchTrigger.Focus);
        Subscribe(onlineManager, RefetchTrigger.Reconnect);
        Subscribe(resumeManager, RefetchTrigger.Resume);

        _connectivity = connectivity;
        if (connectivity is not null)
        {
            _connectivityHandler = online =>
            {
                if (online)
                {
                    _ = RefetchActiveForTriggerAsync(RefetchTrigger.Reconnect);
                }
            };
            connectivity.ConnectivityChanged += _connectivityHandler;
        }
    }

    private void Subscribe(ISignalSource? source, RefetchTrigger trigger)
    {
        if (source is null)
        {
            return;
        }

        void Handler() => _ = RefetchActiveForTriggerAsync(trigger);
        source.Raised += Handler;
        _signalSubscriptions.Add((source, Handler));
    }

    /// <summary>
    /// Refetches every active query that opts in to <paramref name="trigger"/> and is currently stale.
    /// Invoked automatically by focus/online/resume signal sources.
    /// </summary>
    public Task RefetchActiveForTriggerAsync(RefetchTrigger trigger, CancellationToken cancellationToken = default)
    {
        var tasks = new List<Task>();
        foreach (var entry in Cache.GetEntries())
        {
            if (entry.IsActive)
            {
                tasks.Add(entry.RefetchForTriggerAsync(trigger, cancellationToken));
            }
        }

        return tasks.Count == 0 ? Task.CompletedTask : Task.WhenAll(tasks);
    }

    /// <inheritdoc />
    public QueryCache Cache { get; }

    /// <inheritdoc />
    public async Task<QueryResult<T>> FetchAsync<T>(
        QueryKey key,
        Func<CancellationToken, Task<T>> fetcher,
        QueryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(fetcher);

        var resolved = _options.Resolve(options);
        var entry = Cache.GetOrCreate<T>(key);

        if (!resolved.Enabled)
        {
            return new QueryResult<T>(key, entry.CurrentState);
        }

        // Offline: serve cached data if present, otherwise pause without hitting the network.
        // The query refetches automatically when connectivity is restored.
        if (_connectivity is { IsOnline: false })
        {
            return new QueryResult<T>(key, entry.PauseOrServeCached(fetcher, resolved));
        }

        var state = await entry.FetchAsync(fetcher, resolved, forceRefetch: false, cancellationToken).ConfigureAwait(false);
        return new QueryResult<T>(key, state);
    }

    /// <inheritdoc />
    public async Task PrefetchAsync<T>(
        QueryKey key,
        Func<CancellationToken, Task<T>> fetcher,
        QueryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await FetchAsync(key, fetcher, options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Prefetch is best-effort: swallow fetch failures. They are recorded on the entry state.
        }
    }

    /// <inheritdoc />
    public QueryResult<T> GetQueryData<T>(QueryKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        Cache.TryGetState<T>(key, out var state);
        return new QueryResult<T>(key, state);
    }

    /// <inheritdoc />
    public void SetQueryData<T>(QueryKey key, T data)
    {
        ArgumentNullException.ThrowIfNull(key);
        Cache.GetOrCreate<T>(key).SetData(data, _options.DefaultCacheTime);
    }

    /// <inheritdoc />
    public QueryObserver<T> Watch<T>(QueryKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var entry = Cache.GetOrCreate<T>(key);
        QueryObserver<T> observer = null!;
        observer = new QueryObserver<T>(key, o => entry.RemoveObserver((QueryObserver<T>)o));
        entry.AddObserver(observer);
        return observer;
    }

    /// <inheritdoc />
    public Task InvalidateQueriesAsync(QueryKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return InvalidateQueriesAsync(key.IsPrefixOf, cancellationToken);
    }

    /// <inheritdoc />
    public async Task InvalidateQueriesAsync(Func<QueryKey, bool> predicate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var matched = Cache.MarkMatchingStale(predicate);
        var infiniteMatches = MatchingInfiniteQueries(predicate);
        if (matched.Count == 0 && infiniteMatches.Count == 0)
        {
            return;
        }

        if (matched.Count > 0)
        {
            Raise(new QueryInvalidatedEvent { Timestamp = _timeProvider.GetUtcNow(), Keys = matched });
        }

        var refetches = new List<Task>();
        foreach (var key in matched)
        {
            if (Cache.TryGetEntry(key, out var entry) && entry.IsActive)
            {
                refetches.Add(entry.RefetchAsync(cancellationToken));
            }
        }

        foreach (var infinite in infiniteMatches)
        {
            refetches.Add(infinite.RefetchAllAsync(cancellationToken));
        }

        if (refetches.Count > 0)
        {
            await Task.WhenAll(refetches).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Mutation<TVariables, TData> CreateMutation<TVariables, TData>(
        Func<TVariables, CancellationToken, Task<TData>> mutationFn,
        MutationOptions<TVariables, TData>? options = null)
    {
        ArgumentNullException.ThrowIfNull(mutationFn);
        return new Mutation<TVariables, TData>(mutationFn, options, _timeProvider, this);
    }

    /// <inheritdoc />
    public Task<MutationResult<TVariables, TData>> MutateAsync<TVariables, TData>(
        Func<TVariables, CancellationToken, Task<TData>> mutationFn,
        TVariables variables,
        MutationOptions<TVariables, TData>? options = null,
        CancellationToken cancellationToken = default)
    {
        return CreateMutation(mutationFn, options).MutateAsync(variables, cancellationToken);
    }

    /// <inheritdoc />
    public InfiniteQuery<TParam, TPage> CreateInfiniteQuery<TParam, TPage>(
        QueryKey key,
        Func<TParam, CancellationToken, Task<TPage>> fetcher,
        InfiniteQueryOptions<TParam, TPage> options)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(fetcher);
        ArgumentNullException.ThrowIfNull(options);

        var query = new InfiniteQuery<TParam, TPage>(key, fetcher, options, _timeProvider, Unregister);
        lock (_infiniteQueries)
        {
            _infiniteQueries.Add(new WeakReference<IInvalidatableInfiniteQuery>(query));
        }

        return query;

        void Unregister(IInvalidatableInfiniteQuery q)
        {
            lock (_infiniteQueries)
            {
                _infiniteQueries.RemoveAll(w => !w.TryGetTarget(out var t) || ReferenceEquals(t, q));
            }
        }
    }

    private IReadOnlyList<IInvalidatableInfiniteQuery> MatchingInfiniteQueries(Func<QueryKey, bool> predicate)
    {
        var matches = new List<IInvalidatableInfiniteQuery>();
        lock (_infiniteQueries)
        {
            _infiniteQueries.RemoveAll(w => !w.TryGetTarget(out _)); // purge collected queries
            foreach (var weak in _infiniteQueries)
            {
                if (weak.TryGetTarget(out var query) && predicate(query.Key))
                {
                    matches.Add(query);
                }
            }
        }

        return matches;
    }

    /// <inheritdoc />
    public Task RefetchQueryAsync(QueryKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Cache.TryGetEntry(key, out var entry) ? entry.RefetchAsync(cancellationToken) : Task.CompletedTask;
    }

    /// <inheritdoc />
    public bool RemoveQuery(QueryKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Cache.Remove(key);
    }

    /// <inheritdoc />
    public void Clear() => Cache.Clear();

    private void Raise(DotNetQueryEvent @event) => DiagnosticEvent?.Invoke(@event);

    void IQueryEventSink.QueryFetchStarted(QueryKey key)
    {
        DotNetQueryDiagnostics.QueriesStarted.Add(1);
        var activity = DotNetQueryDiagnostics.ActivitySource.StartActivity("dotnetquery.query.fetch", ActivityKind.Client);
        if (activity is not null)
        {
            activity.SetTag("dotnetquery.query.key", key.ToString());
            _activeFetchActivities[key] = activity;
        }

        Raise(new QueryStartedEvent { Timestamp = _timeProvider.GetUtcNow(), Key = key });
    }

    void IQueryEventSink.QueryFetchSucceeded(QueryKey key, TimeSpan duration)
    {
        DotNetQueryDiagnostics.QueriesSucceeded.Add(1);
        DotNetQueryDiagnostics.QueryDuration.Record(duration.TotalMilliseconds);
        if (_activeFetchActivities.TryRemove(key, out var activity))
        {
            activity.SetStatus(ActivityStatusCode.Ok);
            activity.Dispose();
        }

        Raise(new QueryCompletedEvent { Timestamp = _timeProvider.GetUtcNow(), Key = key, Duration = duration });
    }

    void IQueryEventSink.QueryFetchFailed(QueryKey key, Exception error, int failureCount, TimeSpan duration)
    {
        DotNetQueryDiagnostics.QueriesFailed.Add(1);
        DotNetQueryDiagnostics.QueryDuration.Record(duration.TotalMilliseconds);
        if (_activeFetchActivities.TryRemove(key, out var activity))
        {
            activity.SetStatus(ActivityStatusCode.Error, error.Message);
            activity.Dispose();
        }

        Raise(new QueryFailedEvent
        {
            Timestamp = _timeProvider.GetUtcNow(),
            Key = key,
            Error = error,
            FailureCount = failureCount,
            Duration = duration,
        });
    }

    void IQueryEventSink.MutationStarted()
    {
        DotNetQueryDiagnostics.MutationsStarted.Add(1);
        Raise(new MutationStartedEvent { Timestamp = _timeProvider.GetUtcNow() });
    }

    void IQueryEventSink.MutationCompleted(bool succeeded, Exception? error, TimeSpan duration)
    {
        DotNetQueryDiagnostics.MutationsCompleted.Add(1);
        Raise(new MutationCompletedEvent
        {
            Timestamp = _timeProvider.GetUtcNow(),
            Succeeded = succeeded,
            Error = error,
            Duration = duration,
        });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var (source, handler) in _signalSubscriptions)
        {
            source.Raised -= handler;
        }

        _signalSubscriptions.Clear();
        if (_connectivity is not null && _connectivityHandler is not null)
        {
            _connectivity.ConnectivityChanged -= _connectivityHandler;
        }

        _gcTimer.Dispose();
        Cache.Clear();
    }
}
