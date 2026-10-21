using System.Diagnostics;

namespace DotNetQuery;

/// <summary>
/// Default implementation of <see cref="IQueryClient"/>. Thread-safe; all members may be called
/// concurrently.
/// </summary>
/// <remarks>
/// One client owns one cache. Desktop, mobile and Blazor WebAssembly apps typically use a single
/// client for the whole app. Server-side multi-user hosts (Blazor Server / interactive server
/// rendering) must use one client per user circuit so cached data is never shared between users - see
/// <c>AddDotNetQuery(ServiceLifetime.Scoped)</c> and <c>AddDotNetQueryBlazor</c>.
/// </remarks>
public sealed class QueryClient : IQueryClient, IQueryEventSink
{
    private readonly QueryClientOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ITimer? _gcTimer;
    private readonly List<(ISignalSource Source, Action Handler)> _signalSubscriptions = [];
    private readonly IConnectivityProvider? _connectivity;
    private readonly Action<bool>? _connectivityHandler;
    private long _mutationIds;
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
        _connectivity = connectivity;
        Cache = new QueryCache(_timeProvider, this, connectivity, QueryClientOptions.NormalizeDuration(_options.DefaultCacheTime));

        var interval = _options.GarbageCollectionInterval;
        if (interval > TimeSpan.Zero && interval != Timeout.InfiniteTimeSpan)
        {
            _gcTimer = _timeProvider.CreateTimer(
                static state => ((QueryClient)state!).Cache.CollectGarbage(),
                this,
                interval,
                interval);
        }

        Subscribe(focusManager, RefetchTrigger.Focus);
        Subscribe(onlineManager, RefetchTrigger.Reconnect);
        Subscribe(resumeManager, RefetchTrigger.Resume);

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

    /// <inheritdoc />
    public QueryCache Cache { get; }

    /// <summary>Gets the client-wide default options.</summary>
    public QueryClientOptions Options => _options;

    /// <summary>Gets a value indicating whether the client currently considers itself online.</summary>
    public bool IsOnline => _connectivity?.IsOnline ?? true;

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
    /// Refetches every active query that opts in to <paramref name="trigger"/> and is currently stale (and,
    /// on reconnect, every query whose fetch was paused while offline). Invoked automatically by
    /// focus/online/resume signal sources and by the connectivity provider. Never throws for fetch failures.
    /// </summary>
    public Task RefetchActiveForTriggerAsync(RefetchTrigger trigger, CancellationToken cancellationToken = default)
    {
        var tasks = new List<Task>();
        foreach (var entry in Cache.GetEntries())
        {
            var task = entry.RefetchForTriggerAsync(trigger, cancellationToken);
            if (!task.IsCompleted)
            {
                tasks.Add(task);
            }
        }

        return tasks.Count == 0 ? Task.CompletedTask : Task.WhenAll(tasks);
    }

    /// <inheritdoc />
    public Task<QueryResult<T>> FetchAsync<T>(
        QueryKey key,
        Func<CancellationToken, Task<T>> fetcher,
        QueryOptions? options = null,
        bool forceRefetch = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(fetcher);

        var resolved = _options.Resolve(options);
        var mode = forceRefetch ? FetchMode.CancelAndRefetch : FetchMode.IfStale;
        var fetch = Cache.WithEntry<T, Task<QueryState<T>>>(
            key,
            entry => entry.TryFetchAsync(fetcher, resolved, mode, remember: true, resolved.ThrowOnError, cancellationToken));

        return ToResultAsync(key, fetch);
    }

    private static async Task<QueryResult<T>> ToResultAsync<T>(QueryKey key, Task<QueryState<T>> fetch) =>
        new(key, await fetch.ConfigureAwait(false));

    /// <inheritdoc />
    public async Task PrefetchAsync<T>(
        QueryKey key,
        Func<CancellationToken, Task<T>> fetcher,
        QueryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(fetcher);

        // Prefetch is best-effort: failures are recorded on the entry, never thrown.
        var resolved = _options.Resolve(options);
        await Cache.WithEntry<T, Task<QueryState<T>>>(
            key,
            entry => entry.TryFetchAsync(fetcher, resolved, FetchMode.IfStale, remember: true, throwOnError: false, cancellationToken))
            .ConfigureAwait(false);
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
        while (!Cache.GetOrCreate<T>(key).TrySetData(data))
        {
        }
    }

    /// <inheritdoc />
    public T SetQueryData<T>(QueryKey key, Func<T?, T> updater)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(updater);
        while (true)
        {
            if (Cache.GetOrCreate<T>(key).TryUpdateData(updater, out var result))
            {
                return result;
            }
        }
    }

    /// <inheritdoc />
    public bool HydrateQueryData<T>(QueryKey key, T data, DateTimeOffset dataUpdatedAt)
    {
        ArgumentNullException.ThrowIfNull(key);
        while (true)
        {
            if (Cache.GetOrCreate<T>(key).TryHydrate(data, dataUpdatedAt) is { } applied)
            {
                return applied;
            }
        }
    }

    /// <inheritdoc />
    public QueryObserver<T> Watch<T>(QueryKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        while (true)
        {
            var entry = Cache.GetOrCreate<T>(key);
            var observer = new QueryObserver<T>(
                key,
                o => entry.RemoveObserver(o),
                error => ((IQueryEventSink)this).ObserverFailed(key, error));

            if (entry.TryAddObserver(observer))
            {
                return observer;
            }
        }
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
        var matched = Cache.Invalidate(predicate);
        if (matched.Count == 0)
        {
            return;
        }

        Raise(new QueryInvalidatedEvent
        {
            Timestamp = _timeProvider.GetUtcNow(),
            Keys = matched.Select(m => m.Entry.Key).ToArray(),
        });

        var refetches = new List<Task>();
        foreach (var (entry, refetch) in matched)
        {
            if (refetch)
            {
                // Cancel any in-flight fetch: it may have started before the change that prompted this
                // invalidation, so its result could already be outdated.
                refetches.Add(entry.RefetchAsync(FetchMode.CancelAndRefetch, includeDisabled: false, cancellationToken));
            }
        }

        if (refetches.Count > 0)
        {
            await Task.WhenAll(refetches).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task CancelQueriesAsync(QueryKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return CancelQueriesAsync(key.IsPrefixOf, cancellationToken);
    }

    /// <inheritdoc />
    public Task CancelQueriesAsync(Func<QueryKey, bool> predicate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var cancellations = new List<Task>();
        foreach (var entry in Cache.GetEntries())
        {
            if (predicate(entry.Key))
            {
                var task = entry.CancelAsync();
                if (!task.IsCompleted)
                {
                    cancellations.Add(task);
                }
            }
        }

        return cancellations.Count == 0
            ? Task.CompletedTask
            : Task.WhenAll(cancellations).WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Mutation<TVariables, TData> CreateMutation<TVariables, TData>(
        Func<TVariables, CancellationToken, Task<TData>> mutationFn,
        MutationOptions<TVariables, TData>? options = null)
    {
        ArgumentNullException.ThrowIfNull(mutationFn);
        return new Mutation<TVariables, TData>(mutationFn, options, _timeProvider, this, this);
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

        var resolved = _options.Resolve(options.QueryOptions);
        if (options.QueryOptions?.RetryCount is null)
        {
            resolved = resolved with { RetryCount = Math.Max(0, options.RetryCount) };
        }

        if (options.QueryOptions?.RetryDelay is null)
        {
            resolved = resolved with { RetryDelay = options.RetryDelay ?? RetryPolicy.Constant(TimeSpan.Zero) };
        }

        return new InfiniteQuery<TParam, TPage>(this, key, fetcher, options, resolved);
    }

    /// <inheritdoc />
    public Task RefetchQueryAsync(QueryKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Cache.TryGetEntry(key, out var entry)
            ? entry.RefetchAsync(FetchMode.CancelAndRefetch, includeDisabled: true, cancellationToken)
            : Task.CompletedTask;
    }

    /// <inheritdoc />
    public bool RemoveQuery(QueryKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Cache.Remove(key);
    }

    /// <inheritdoc />
    public void Clear() => Cache.Clear();

    private void Raise(DotNetQueryEvent @event)
    {
        var handlers = DiagnosticEvent;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<DotNetQueryEvent>)handler)(@event);
            }
            catch (Exception)
            {
                // Diagnostics must never break the operation being observed.
            }
        }
    }

    object? IQueryEventSink.QueryFetchStarted(QueryKey key)
    {
        DotNetQueryDiagnostics.QueriesStarted.Add(1);
        var activity = DotNetQueryDiagnostics.ActivitySource.StartActivity("dotnetquery.query.fetch", ActivityKind.Internal);
        activity?.SetTag("dotnetquery.query.key", key.ToString());
        Raise(new QueryStartedEvent { Timestamp = _timeProvider.GetUtcNow(), Key = key });
        return activity;
    }

    void IQueryEventSink.QueryFetchSucceeded(QueryKey key, TimeSpan duration, object? token)
    {
        DotNetQueryDiagnostics.QueriesSucceeded.Add(1);
        DotNetQueryDiagnostics.QueryDuration.Record(duration.TotalMilliseconds);
        StopActivity(token, ActivityStatusCode.Ok, null);
        Raise(new QueryCompletedEvent { Timestamp = _timeProvider.GetUtcNow(), Key = key, Duration = duration });
    }

    void IQueryEventSink.QueryFetchFailed(QueryKey key, Exception error, int failureCount, TimeSpan duration, object? token)
    {
        DotNetQueryDiagnostics.QueriesFailed.Add(1);
        DotNetQueryDiagnostics.QueryDuration.Record(duration.TotalMilliseconds);
        StopActivity(token, ActivityStatusCode.Error, error.Message);
        Raise(new QueryFailedEvent
        {
            Timestamp = _timeProvider.GetUtcNow(),
            Key = key,
            Error = error,
            FailureCount = failureCount,
            Duration = duration,
        });
    }

    void IQueryEventSink.QueryFetchCancelled(QueryKey key, TimeSpan duration, object? token)
    {
        DotNetQueryDiagnostics.QueriesCancelled.Add(1);
        if (token is Activity activity)
        {
            activity.SetTag("dotnetquery.query.cancelled", true);
            activity.Dispose();
        }

        Raise(new QueryCancelledEvent { Timestamp = _timeProvider.GetUtcNow(), Key = key, Duration = duration });
    }

    void IQueryEventSink.ObserverFailed(QueryKey key, Exception error) =>
        Raise(new QueryObserverErrorEvent { Timestamp = _timeProvider.GetUtcNow(), Key = key, Error = error });

    (long Id, object? Token) IQueryEventSink.MutationStarted(QueryKey? mutationKey, object? variables)
    {
        var id = Interlocked.Increment(ref _mutationIds);
        DotNetQueryDiagnostics.MutationsStarted.Add(1);
        var activity = DotNetQueryDiagnostics.ActivitySource.StartActivity("dotnetquery.mutation", ActivityKind.Internal);
        if (activity is not null)
        {
            activity.SetTag("dotnetquery.mutation.id", id);
            if (mutationKey is not null)
            {
                activity.SetTag("dotnetquery.mutation.key", mutationKey.ToString());
            }
        }

        Raise(new MutationStartedEvent
        {
            Timestamp = _timeProvider.GetUtcNow(),
            MutationId = id,
            MutationKey = mutationKey,
            Variables = variables,
        });
        return (id, activity);
    }

    void IQueryEventSink.MutationCompleted(long id, QueryKey? mutationKey, bool succeeded, Exception? error, int failureCount, TimeSpan duration, object? token)
    {
        DotNetQueryDiagnostics.MutationsCompleted.Add(1);
        if (!succeeded)
        {
            DotNetQueryDiagnostics.MutationsFailed.Add(1);
        }

        DotNetQueryDiagnostics.MutationDuration.Record(duration.TotalMilliseconds);
        StopActivity(token, succeeded ? ActivityStatusCode.Ok : ActivityStatusCode.Error, error?.Message);
        Raise(new MutationCompletedEvent
        {
            Timestamp = _timeProvider.GetUtcNow(),
            MutationId = id,
            MutationKey = mutationKey,
            Succeeded = succeeded,
            Error = error,
            FailureCount = failureCount,
            Duration = duration,
        });
    }

    private static void StopActivity(object? token, ActivityStatusCode status, string? description)
    {
        if (token is Activity activity)
        {
            activity.SetStatus(status, description);
            activity.Dispose();
        }
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

        _gcTimer?.Dispose();
        Cache.Clear();
    }
}
