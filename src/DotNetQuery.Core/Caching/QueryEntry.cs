namespace DotNetQuery;

/// <summary>
/// A single typed cache entry. Owns the query's current <see cref="QueryState{T}"/>,
/// deduplicates concurrent fetches, applies the retry policy, and notifies observers.
/// </summary>
/// <typeparam name="T">The type of data produced by the query.</typeparam>
internal sealed class QueryEntry<T> : IQueryEntry
{
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly IQueryEventSink? _eventSink;
    private readonly HashSet<QueryObserver<T>> _observers = new();

    private QueryState<T> _state = QueryState<T>.Idle;
    private Task? _inFlight;
    private CancellationTokenSource? _inFlightCts;
    private bool _isStale = true;
    private DateTimeOffset _lastActivityAt;
    private Func<CancellationToken, Task<T>>? _lastFetcher;
    private ResolvedQueryOptions _lastOptions;
    private ITimer? _refreshTimer;
    private int _fetchCount;

    public QueryEntry(QueryKey key, TimeProvider timeProvider, IQueryEventSink? eventSink = null)
    {
        Key = key;
        _timeProvider = timeProvider;
        _eventSink = eventSink;
        _lastActivityAt = timeProvider.GetUtcNow();
    }

    public QueryKey Key { get; }

    public TimeSpan CacheTime { get; private set; } = TimeSpan.FromMinutes(5);

    public DateTimeOffset LastActivityAt
    {
        get { lock (_gate) { return _lastActivityAt; } }
    }

    public int ObserverCount
    {
        get { lock (_gate) { return _observers.Count; } }
    }

    public bool IsActive => ObserverCount > 0;

    /// <summary>Gets an immutable snapshot of the entry's current state.</summary>
    public QueryState<T> CurrentState
    {
        get { lock (_gate) { return _state; } }
    }

    public void MarkStale()
    {
        lock (_gate)
        {
            _isStale = true;
        }
    }

    public Task RefetchAsync(CancellationToken cancellationToken)
    {
        Func<CancellationToken, Task<T>>? fetcher;
        ResolvedQueryOptions options;
        lock (_gate)
        {
            fetcher = _lastFetcher;
            options = _lastOptions;
        }

        return fetcher is null ? Task.CompletedTask : FetchAsync(fetcher, options, forceRefetch: true, cancellationToken);
    }

    public Task RefetchForTriggerAsync(RefetchTrigger trigger, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_lastFetcher is null)
            {
                return Task.CompletedTask;
            }

            var optedIn = trigger switch
            {
                RefetchTrigger.Focus => _lastOptions.RefetchOnFocus,
                RefetchTrigger.Reconnect => _lastOptions.RefetchOnReconnect,
                RefetchTrigger.Resume => _lastOptions.RefetchOnResume,
                _ => false,
            };

            if (!optedIn || (!_isStale && IsFresh(_lastOptions.StaleTime)))
            {
                return Task.CompletedTask;
            }
        }

        return RefetchAsync(cancellationToken);
    }

    /// <summary>
    /// Records the fetcher/options and, when offline, serves cached data if present or transitions to
    /// the <see cref="QueryStatus.Paused"/> state without invoking the fetcher. The stored fetcher lets
    /// a later reconnect refetch the query.
    /// </summary>
    public QueryState<T> PauseOrServeCached(Func<CancellationToken, Task<T>> fetcher, ResolvedQueryOptions options)
    {
        lock (_gate)
        {
            _lastFetcher = fetcher;
            _lastOptions = options;
            _isStale = true;
            CacheTime = options.CacheTime;
        }

        if (!CurrentState.HasData)
        {
            SetState(previous => previous with { Status = QueryStatus.Paused });
        }

        return CurrentState;
    }

    /// <summary>Imperatively sets the entry's data, marking it fresh and notifying observers.</summary>
    public void SetData(T data, TimeSpan cacheTime)
    {
        var now = _timeProvider.GetUtcNow();
        CacheTime = cacheTime;
        lock (_gate)
        {
            _isStale = false;
        }

        SetState(previous => previous with
        {
            Status = QueryStatus.Success,
            Data = data,
            Error = null,
            DataUpdatedAt = now,
            FailureCount = 0,
        });
    }

    /// <summary>
    /// Fetches data for this entry, deduplicating with any in-flight fetch and serving fresh
    /// cached data without a network call. Honors the caller's <paramref name="cancellationToken"/>
    /// without cancelling the shared fetch that other callers may be awaiting.
    /// </summary>
    public async Task<QueryState<T>> FetchAsync(
        Func<CancellationToken, Task<T>> fetcher,
        ResolvedQueryOptions options,
        bool forceRefetch,
        CancellationToken cancellationToken)
    {
        Touch();
        CacheTime = options.CacheTime;

        Task shared;
        lock (_gate)
        {
            _lastFetcher = fetcher;
            _lastOptions = options;
            UpdateRefetchSchedule();

            if (!forceRefetch && !_isStale && IsFresh(options.StaleTime))
            {
                return _state;
            }

            shared = _inFlight ??= RunFetchCycleAsync(fetcher, options);
        }

        await shared.WaitAsync(cancellationToken).ConfigureAwait(false);

        QueryState<T> snapshot = CurrentState;
        if (options.ThrowOnError && snapshot is { Status: QueryStatus.Error, Error: not null })
        {
            throw snapshot.Error;
        }

        return snapshot;
    }

    private bool IsFresh(TimeSpan staleTime)
    {
        if (_state.Status is not (QueryStatus.Success or QueryStatus.Refetching) || _state.DataUpdatedAt is not { } updatedAt)
        {
            return false;
        }

        return _timeProvider.GetUtcNow() - updatedAt < staleTime;
    }

    private async Task RunFetchCycleAsync(Func<CancellationToken, Task<T>> fetcher, ResolvedQueryOptions options)
    {
        // Yield so the caller returns to the lock section and observes _inFlight before we mutate state.
        await Task.Yield();

        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _isStale = false;
            _inFlightCts = cts;
            _fetchCount++;
        }

        SetState(previous => previous with
        {
            Status = previous.HasData ? QueryStatus.Refetching : QueryStatus.Loading,
            FailureCount = 0,
        });

        _eventSink?.QueryFetchStarted(Key);
        var startTimestamp = _timeProvider.GetTimestamp();

        var attempt = 0;
        try
        {
            while (true)
            {
                try
                {
                    var data = await fetcher(cts.Token).ConfigureAwait(false);
                    var now = _timeProvider.GetUtcNow();
                    SetState(previous => previous with
                    {
                        Status = QueryStatus.Success,
                        Data = data,
                        Error = null,
                        DataUpdatedAt = now,
                        FailureCount = 0,
                    });
                    _eventSink?.QueryFetchSucceeded(Key, _timeProvider.GetElapsedTime(startTimestamp));
                    return;
                }
                catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
                {
                    return; // Entry-level cancellation (disposed/replaced); leave last good state intact.
                }
                catch (Exception ex)
                {
                    attempt++;
                    SetState(previous => previous with { FailureCount = attempt });

                    if (attempt > options.RetryCount)
                    {
                        var now = _timeProvider.GetUtcNow();
                        SetState(previous => previous with
                        {
                            Status = QueryStatus.Error,
                            Error = ex,
                            ErrorUpdatedAt = now,
                        });
                        _eventSink?.QueryFetchFailed(Key, ex, attempt, _timeProvider.GetElapsedTime(startTimestamp));
                        return;
                    }

                    var delay = options.RetryDelay(attempt - 1, ex);
                    if (delay > TimeSpan.Zero)
                    {
                        try
                        {
                            await Task.Delay(delay, _timeProvider, cts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                    }
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _inFlight = null;
                _inFlightCts = null;
            }

            cts.Dispose();
        }
    }

    internal void AddObserver(QueryObserver<T> observer)
    {
        QueryState<T> current;
        lock (_gate)
        {
            _observers.Add(observer);
            _lastActivityAt = _timeProvider.GetUtcNow();
            current = _state;
            UpdateRefetchSchedule();
        }

        observer.NotifyStateChanged(current);
    }

    internal void RemoveObserver(QueryObserver<T> observer)
    {
        CancellationTokenSource? cancelInFlight = null;
        lock (_gate)
        {
            _observers.Remove(observer);
            _lastActivityAt = _timeProvider.GetUtcNow();
            UpdateRefetchSchedule();

            // When the last observer leaves mid-fetch and the query opts in, abort the shared fetch so
            // work for a no-longer-visible query stops. The last good state is left intact.
            if (_observers.Count == 0 && _inFlight is not null && _lastOptions.CancelInFlightWhenUnobserved)
            {
                cancelInFlight = _inFlightCts;
            }
        }

        cancelInFlight?.Cancel();
    }

    /// <summary>
    /// Starts, updates, or stops the periodic background refetch timer based on whether the entry
    /// has active observers and a configured <see cref="ResolvedQueryOptions.RefetchInterval"/>.
    /// Must be called while holding <see cref="_gate"/>.
    /// </summary>
    private void UpdateRefetchSchedule()
    {
        var shouldRun = _observers.Count > 0
            && _lastOptions.RefetchInterval is { } configured
            && configured > TimeSpan.Zero;

        if (shouldRun)
        {
            var interval = _lastOptions.RefetchInterval!.Value;
            if (_refreshTimer is null)
            {
                _refreshTimer = _timeProvider.CreateTimer(
                    static state => _ = ((QueryEntry<T>)state!).RefetchAsync(CancellationToken.None),
                    this,
                    interval,
                    interval);
            }
            else
            {
                _refreshTimer.Change(interval, interval);
            }
        }
        else
        {
            _refreshTimer?.Dispose();
            _refreshTimer = null;
        }
    }

    public QueryDescriptor Describe()
    {
        lock (_gate)
        {
            var stale = _isStale || !IsFresh(_lastOptions.StaleTime);
            return new QueryDescriptor
            {
                Key = Key,
                Status = _state.Status,
                DataTypeName = typeof(T).Name,
                HasData = _state.HasData,
                IsStale = stale,
                ObserverCount = _observers.Count,
                IsActive = _observers.Count > 0,
                FailureCount = _state.FailureCount,
                FetchCount = _fetchCount,
                ErrorMessage = _state.Error?.Message,
                DataUpdatedAt = _state.DataUpdatedAt,
                ErrorUpdatedAt = _state.ErrorUpdatedAt,
                LastActivityAt = _lastActivityAt,
                CacheTime = CacheTime,
            };
        }
    }

    /// <summary>Cancels any in-flight fetch. Called by the cache when the entry is garbage collected.</summary>
    public void Dispose()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _inFlightCts;
            _refreshTimer?.Dispose();
            _refreshTimer = null;
        }

        cts?.Cancel();
    }

    private void Touch()
    {
        lock (_gate)
        {
            _lastActivityAt = _timeProvider.GetUtcNow();
        }
    }

    private void SetState(Func<QueryState<T>, QueryState<T>> mutate)
    {
        QueryState<T> newState;
        QueryObserver<T>[] observers;
        lock (_gate)
        {
            newState = mutate(_state);
            _state = newState;
            observers = _observers.Count == 0 ? [] : [.. _observers];
        }

        foreach (var observer in observers)
        {
            observer.NotifyStateChanged(newState);
        }
    }
}
