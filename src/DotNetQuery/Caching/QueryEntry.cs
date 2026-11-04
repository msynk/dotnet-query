using System.Runtime.ExceptionServices;

namespace DotNetQuery;

/// <summary>
/// A single typed cache entry. Owns the query's current <see cref="QueryState{T}"/>, runs and
/// deduplicates fetch cycles (with retries, cancellation and offline pausing), and notifies observers.
/// </summary>
/// <remarks>
/// All mutable state is guarded by <see cref="_gate"/>. User code (fetchers, observer handlers, cache
/// change handlers) never runs while the gate is held: each state transition is recorded under the lock
/// into an outbox, and the outbox is drained afterwards by a single thread at a time, so subscribers see
/// every transition exactly once and in order, even when changes race on different threads.
/// </remarks>
/// <typeparam name="T">The type of data produced by the query.</typeparam>
internal sealed partial class QueryEntry<T> : IQueryEntry
{
    // Task.Delay rejects delays above ~49.7 days; clamp misbehaving retry strategies instead of crashing.
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
    private static long s_nextId;

    private readonly object _gate = new();
    private readonly QueryRuntime _runtime;
    private readonly HashSet<IQuerySubscriber<T>> _observers = [];
    private readonly Queue<Pending> _outbox = new();

    private QueryState<T> _state = QueryState<T>.Idle;

    // Completed tasks for the current state, reused while the state is unchanged (fresh cache hits return them).
    private Task<QueryState<T>>? _stateTask;
    private Task<QueryResult<T>>? _resultTask;
    private long _version;
    private FetchCycle? _cycle;
    private bool _invalidated;
    private bool _pausedFetchPending;
    private Func<CancellationToken, Task<T>>? _lastFetcher;
    private ResolvedQueryOptions _lastOptions;
    private ITimer? _refreshTimer;
    private TimeSpan _refreshInterval;
    private int _fetchCount;
    private DateTimeOffset _lastActivityAt;
    private TimeSpan _cacheTime;
    private bool _removed;
    private bool _draining;
    private Func<T>? _initialData;
    private TimeSpan? _adaptiveInterval;
    private Action? _drainNow;
    private FetchCycle? _detachedCycle;
    private DateTimeOffset? _initialDataUpdatedAt;

    public QueryEntry(QueryKey key, QueryRuntime runtime)
    {
        Key = key;
        _runtime = runtime;
        _lastActivityAt = runtime.TimeProvider.GetUtcNow();
        _cacheTime = runtime.DefaultCacheTime;
    }

    public QueryKey Key { get; }

    /// <summary>A process-unique id giving entries a global lock order for multi-entry transactions.</summary>
    internal long Id { get; } = Interlocked.Increment(ref s_nextId);

    /// <summary>The entry lock, taken directly (in <see cref="Id"/> order) only by multi-entry transactions.</summary>
    internal object Gate => _gate;

    public TimeSpan CacheTime
    {
        get { lock (_gate) { return _cacheTime; } }
    }

    public DateTimeOffset LastActivityAt
    {
        get { lock (_gate) { return _lastActivityAt; } }
    }

    public int ObserverCount
    {
        get { lock (_gate) { return _observers.Count; } }
    }

    public bool IsActive => ObserverCount > 0;

    public bool IsRemoved
    {
        get { lock (_gate) { return _removed; } }
    }

    public bool IsFetching
    {
        get { lock (_gate) { return _cycle is not null; } }
    }

    /// <summary>Gets an immutable snapshot of the entry's current state.</summary>
    public QueryState<T> CurrentState
    {
        get { lock (_gate) { return _state; } }
    }

    private DateTimeOffset Now => _runtime.TimeProvider.GetUtcNow();

    // ---------------------------------------------------------------- fetching

    /// <summary>
    /// Fetches data for this entry according to <paramref name="mode"/>. Returns <see langword="null"/> when
    /// the entry has been removed from the cache (the caller retries against a fresh entry).
    /// </summary>
    /// <param name="fetcher">The fetcher to run if a new fetch cycle starts.</param>
    /// <param name="options">The effective options of the request.</param>
    /// <param name="mode">How to treat freshness and an in-flight fetch.</param>
    /// <param name="remember">
    /// Whether to record <paramref name="fetcher"/>/<paramref name="options"/> as the entry's refetch recipe
    /// (used by invalidation, intervals, and focus/reconnect triggers).
    /// </param>
    /// <param name="throwOnError">Whether to rethrow a terminal fetch error to this caller.</param>
    /// <param name="cancellationToken">Cancels this caller's wait only; shared work continues.</param>
    /// <param name="deferWhileOptimistic">
    /// Whether this is a refresh that waits while optimistic writes of pending mutations are on screen (see
    /// <c>QueryEntry.Optimistic.cs</c>); <see langword="false"/> for fetches that extend the data instead of replacing it
    /// (an infinite query's next or previous page).
    /// </param>
    public Task<QueryState<T>>? TryFetchAsync(
        Func<CancellationToken, Task<T>> fetcher,
        ResolvedQueryOptions options,
        FetchMode mode,
        bool remember,
        bool throwOnError,
        CancellationToken cancellationToken,
        bool deferWhileOptimistic = true)
    {
        CancellationTokenSource? superseded = null;
        FetchCycle? cycle = null;
        QueryState<T> snapshot;

        lock (_gate)
        {
            if (_removed)
            {
                return null;
            }

            _lastActivityAt = Now;
            if (remember)
            {
                _lastFetcher = fetcher;
                _lastOptions = options;
                _cacheTime = options.CacheTime;
                UpdateRefetchScheduleLocked();
            }

            if (!options.Enabled)
            {
                return StateTaskLocked();
            }

            if (_cycle is { } inFlight && !inFlight.Cts.IsCancellationRequested && mode is not FetchMode.CancelAndRefetch)
            {
                cycle = inFlight; // deduplicate (never join a fetch that is already being cancelled)
            }
            else if (mode is FetchMode.IfStale && !IsStaleLocked(options.StaleTime))
            {
                return StateTaskLocked();
            }
            else if (deferWhileOptimistic && HasPendingLayersLocked())
            {
                // Optimistic writes of pending mutations are on screen: server data fetched now would not include them
                // and would replace them until those mutations settle. Refetch once they have settled instead (an
                // in-flight fetch being replaced is outdated as well).
                _refetchDeferred = true;
                _invalidated = true;
                if (_cycle is { } outdated)
                {
                    superseded = outdated.Cts;
                }
            }
            else if (options.NetworkMode is NetworkMode.Online && !_runtime.IsOnline)
            {
                // Offline: never hit the network. Serve cached data if any, otherwise pause; either way the
                // fetch resumes automatically on reconnect.
                _pausedFetchPending = true;
                if (_cycle is { } outdated && mode is FetchMode.CancelAndRefetch)
                {
                    // The in-flight result predates whatever prompted this refetch: drop it (the previous
                    // state is restored) and let the paused fetch run on reconnect instead.
                    superseded = outdated.Cts;
                }
                else if (_cycle is null && !_state.HasData && _state.Status is not QueryStatus.Paused)
                {
                    SetStateLocked(_state with { Status = QueryStatus.Paused }, dataChanged: false);
                }
            }
            else
            {
                cycle = StartCycleLocked(fetcher, options, out superseded);
            }

            snapshot = _state;
        }

        CancelQuietly(superseded);
        Drain();
        return cycle is null ? Task.FromResult(snapshot) : AwaitCycleAsync(cycle, throwOnError, cancellationToken);
    }

    /// <summary>
    /// <see cref="TryFetchAsync"/> for <c>IQueryClient.FetchAsync</c>: remembers the fetcher and returns the result;
    /// a request served from the cache returns a completed task shared while the state is unchanged.
    /// </summary>
    public Task<QueryResult<T>>? TryFetchResultAsync(
        Func<CancellationToken, Task<T>> fetcher,
        ResolvedQueryOptions options,
        FetchMode mode,
        CancellationToken cancellationToken)
    {
        var fetch = TryFetchAsync(fetcher, options, mode, remember: true, options.ThrowOnError, cancellationToken);
        if (fetch is null)
        {
            return null;
        }

        if (!fetch.IsCompletedSuccessfully)
        {
            return ToResultAsync(Key, fetch);
        }

        var state = fetch.Result;
        var cached = Volatile.Read(ref _resultTask);
        if (cached is not null && ReferenceEquals(cached.Result.State, state))
        {
            return cached;
        }

        cached = Task.FromResult(new QueryResult<T>(Key, state));
        Volatile.Write(ref _resultTask, cached);
        return cached;

        static async Task<QueryResult<T>> ToResultAsync(QueryKey key, Task<QueryState<T>> fetch) =>
            new(key, await fetch.ConfigureAwait(false));
    }

    private Task<QueryState<T>> StateTaskLocked()
    {
        var task = _stateTask;
        if (task is null || !ReferenceEquals(task.Result, _state))
        {
            _stateTask = task = Task.FromResult(_state);
        }

        return task;
    }

    /// <summary>
    /// Refetches with the remembered fetcher/options. Never throws for fetch failures. Disabled queries are
    /// skipped unless <paramref name="includeDisabled"/> is set.
    /// </summary>
    public Task RefetchAsync(FetchMode mode, bool includeDisabled, CancellationToken cancellationToken)
    {
        Func<CancellationToken, Task<T>>? fetcher;
        ResolvedQueryOptions options;
        lock (_gate)
        {
            fetcher = _lastFetcher;
            options = _lastOptions;
        }

        if (fetcher is null)
        {
            return Task.CompletedTask;
        }

        if (!options.Enabled)
        {
            if (!includeDisabled)
            {
                return Task.CompletedTask;
            }

            options = options with { Enabled = true };
        }

        return (Task?)TryFetchAsync(fetcher, options, mode, remember: false, throwOnError: false, cancellationToken)
            ?? Task.CompletedTask;
    }

    public Task RefetchForTriggerAsync(RefetchTrigger trigger, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_lastFetcher is null || !_lastOptions.Enabled)
            {
                return Task.CompletedTask;
            }

            // A fetch that was paused while offline resumes on reconnect even without observers or opt-in:
            // it is a deferred fetch, not a background refetch.
            var resumePaused = trigger is RefetchTrigger.Reconnect && _pausedFetchPending;
            if (!resumePaused)
            {
                var optedIn = trigger switch
                {
                    RefetchTrigger.Focus => _lastOptions.RefetchOnFocus,
                    RefetchTrigger.Reconnect => _lastOptions.RefetchOnReconnect,
                    RefetchTrigger.Resume => _lastOptions.RefetchOnResume,
                    _ => false,
                };

                if (_observers.Count == 0 || !optedIn || !IsStaleLocked(_lastOptions.StaleTime))
                {
                    return Task.CompletedTask;
                }
            }
        }

        return RefetchAsync(FetchMode.Force, includeDisabled: false, cancellationToken);
    }

    /// <summary>Marks the entry invalidated. Returns whether it should be refetched now (active and enabled).</summary>
    public bool Invalidate()
    {
        lock (_gate)
        {
            _invalidated = true;
            return _observers.Count > 0 && _lastFetcher is not null && _lastOptions.Enabled;
        }
    }

    /// <summary>Cancels the in-flight fetch (if any), restoring the previous state. Completes once it has settled.</summary>
    public Task CancelAsync()
    {
        FetchCycle? cycle;
        lock (_gate)
        {
            cycle = _cycle;
        }

        if (cycle is null)
        {
            return Task.CompletedTask;
        }

        CancelQuietly(cycle.Cts);
        return cycle.Completion;
    }

    public Task ResetAsync()
    {
        ResetAndDetach()();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Resets the entry like <see cref="ResetAsync"/>, but returns the cancellation of the detached in-flight fetch
    /// instead of performing it, so a caller can start the follow-up refetch first: the next cycle that starts is
    /// linked as the detached cycle's successor, and its waiters receive that refetch's outcome deterministically.
    /// </summary>
    public Action ResetAndDetach()
    {
        Func<T>? initialData;
        DateTimeOffset? initialDataUpdatedAt;
        lock (_gate)
        {
            if (_removed)
            {
                return static () => { };
            }

            initialData = _initialData;
            initialDataUpdatedAt = _initialDataUpdatedAt;
        }

        var hasInitial = false;
        T? initialValue = default;
        if (initialData is not null)
        {
            try
            {
                initialValue = initialData();
                hasInitial = true;
            }
            catch (Exception)
            {
                // A failing initial-data factory resets to the empty state instead.
            }
        }

        CancellationTokenSource? cancel = null;
        lock (_gate)
        {
            if (_removed)
            {
                return static () => { };
            }

            // Detach the in-flight fetch: it is cancelled and, whatever its outcome, never touches state again (it
            // must neither restore its pre-fetch status nor land outdated data over the reset). Its waiters receive
            // the entry's state as of when it settles.
            if (_cycle is { } cycle)
            {
                cycle.Detached = true;
                cancel = cycle.Cts;
                _cycle = null;
                _detachedCycle = cycle;
            }

            _invalidated = false;
            _pausedFetchPending = false;
            SetStateLocked(
                hasInitial
                    ? new QueryState<T> { Status = QueryStatus.Success, Data = initialValue, DataUpdatedAt = initialDataUpdatedAt ?? Now }
                    : QueryState<T>.Idle,
                dataChanged: true);
        }

        Drain();
        return () => CancelQuietly(cancel);
    }

    /// <summary>
    /// Marks the entry removed and cancels its in-flight fetch - but only if nothing observes it. Returns
    /// <see langword="false"/> (changing nothing) for an observed entry: removing it would strand its observers on a
    /// dead entry that never updates again.
    /// </summary>
    public bool TryRemoveUnobserved()
    {
        CancellationTokenSource? cancel;
        lock (_gate)
        {
            if (_removed)
            {
                return true;
            }

            if (_observers.Count > 0)
            {
                return false;
            }

            _removed = true;
            _refreshTimer?.Dispose();
            _refreshTimer = null;
            cancel = _cycle?.Cts;
        }

        CancelQuietly(cancel);
        return true;
    }

    /// <summary>
    /// Seeds <paramref name="factory"/>'s data when the entry holds no data and is not in an error state, and
    /// remembers it as the entry's initial data for <see cref="ResetAsync"/>. The factory runs outside the lock.
    /// Returns <see langword="null"/> if the entry was removed, otherwise whether the data was applied.
    /// </summary>
    public bool? TrySeedInitialData(Func<T> factory, DateTimeOffset? updatedAt)
    {
        lock (_gate)
        {
            if (_removed)
            {
                return null;
            }

            _initialData = factory;
            _initialDataUpdatedAt = updatedAt;
            if (_state.HasData || _state.Status is QueryStatus.Error)
            {
                return false;
            }
        }

        var data = factory();

        lock (_gate)
        {
            if (_removed)
            {
                return null;
            }

            if (_state.HasData || _state.Status is QueryStatus.Error)
            {
                return false;
            }

            ApplyDataLocked(data, updatedAt ?? Now);
        }

        Drain();
        return true;
    }

    public bool Matches(QueryFilter filter)
    {
        if (!filter.MatchesKey(Key))
        {
            return false;
        }

        lock (_gate)
        {
            if (_removed || !filter.MatchesState(
                    isActive: _observers.Count > 0,
                    isStale: IsStaleLocked(_lastFetcher is null ? TimeSpan.Zero : _lastOptions.StaleTime),
                    isFetching: _cycle is not null,
                    status: _state.Status))
            {
                return false;
            }
        }

        return filter.Predicate is null || filter.Predicate(Describe());
    }

    private FetchCycle StartCycleLocked(
        Func<CancellationToken, Task<T>> fetcher,
        ResolvedQueryOptions options,
        out CancellationTokenSource? superseded)
    {
        var previous = _cycle;
        var cycle = new FetchCycle(fetcher, options)
        {
            // A superseding cycle inherits what to restore from the cycle it replaces.
            RevertStatus = previous?.RevertStatus ?? _state.Status,
            RevertInvalidated = previous?.RevertInvalidated ?? _invalidated,
        };

        superseded = null;
        if (previous is not null)
        {
            previous.Successor = cycle;
            superseded = previous.Cts;
        }

        // The first fetch after a reset takes over the waiters of the fetch the reset detached.
        if (_detachedCycle is { } detached)
        {
            detached.Successor ??= cycle;
            _detachedCycle = null;
        }

        _cycle = cycle;
        _invalidated = false;
        _pausedFetchPending = false;
        _refetchDeferred = false;
        _fetchCount++;

        // Transition synchronously so the very first render after a fetch starts already shows Loading.
        SetStateLocked(
            _state with { Status = _state.HasData ? QueryStatus.Refetching : QueryStatus.Loading, FailureCount = 0 },
            dataChanged: false);

        // Run on the thread pool: never invoke the fetcher under the lock or on a UI synchronization context.
        cycle.Completion = Task.Run(() => RunCycleAsync(cycle));
        return cycle;
    }

    private async Task RunCycleAsync(FetchCycle cycle)
    {
        var token = cycle.Cts.Token;
        var sink = _runtime.EventSink;
        var sinkToken = sink?.QueryFetchStarted(Key);
        var started = _runtime.TimeProvider.GetTimestamp();

        T data = default!;
        Exception? error = null;
        var attempts = 0;
        FetchOutcome outcome;

        while (true)
        {
            try
            {
                data = await RunAttemptAsync(cycle, attempts + 1, token).ConfigureAwait(false);

                // A fetcher that ignores its token may still complete after being cancelled/superseded.
                outcome = token.IsCancellationRequested ? FetchOutcome.Cancelled : FetchOutcome.Succeeded;
                break;
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                outcome = FetchOutcome.Cancelled;
                break;
            }
            catch (Exception ex)
            {
                attempts++;
                error = ex;
                if (attempts > cycle.Options.RetryCount || !ShouldRetry(cycle.Options.ShouldRetry, attempts, ex))
                {
                    outcome = FetchOutcome.Failed;
                    break;
                }

                if (cycle.Options.NetworkMode is not NetworkMode.Always && !_runtime.IsOnline)
                {
                    // Lost connectivity mid-fetch: stop burning retries and resume on reconnect.
                    outcome = FetchOutcome.Paused;
                    break;
                }

                ReportRetry(cycle, attempts, ex);

                TimeSpan delay;
                try
                {
                    delay = cycle.Options.RetryDelay(attempts - 1, ex);
                }
                catch (Exception)
                {
                    outcome = FetchOutcome.Failed; // a throwing retry strategy ends the cycle with the fetch error
                    break;
                }

                if (delay > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(delay < MaxRetryDelay ? delay : MaxRetryDelay, _runtime.TimeProvider, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        outcome = FetchOutcome.Cancelled;
                        break;
                    }
                }

                if (token.IsCancellationRequested)
                {
                    outcome = FetchOutcome.Cancelled;
                    break;
                }
            }
        }

        if (outcome is FetchOutcome.Succeeded && cycle.Options.StructuralSharing)
        {
            // Runs Equals (user code) outside the lock. If the entry changes meanwhile, the shared value is
            // still equal to what was fetched, so storing it is as correct as storing the fetched value.
            data = StructuralSharing.Share(CurrentData(), data);
        }

        Complete(cycle, outcome, data, error, attempts);
        UpdateAdaptiveInterval(cycle.Options);

        if (sink is not null)
        {
            var elapsed = _runtime.TimeProvider.GetElapsedTime(started);
            switch (outcome)
            {
                case FetchOutcome.Succeeded:
                    sink.QueryFetchSucceeded(Key, cycle.Options.Meta, elapsed, sinkToken);
                    break;
                case FetchOutcome.Failed:
                    sink.QueryFetchFailed(Key, cycle.Options.Meta, error!, attempts, elapsed, sinkToken);
                    break;
                default:
                    sink.QueryFetchCancelled(Key, elapsed, sinkToken);
                    break;
            }
        }

        cycle.Cts.Dispose();
    }

    // A predicate that throws vetoes the retry: the cycle ends with the fetch error, not the predicate's.
    private static bool ShouldRetry(RetryPredicate? predicate, int failureCount, Exception error)
    {
        if (predicate is null)
        {
            return true;
        }

        try
        {
            return predicate(failureCount, error);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // One attempt through the client's limiters, timeout and interceptors. Without any of them configured (the
    // default), the fetcher is called directly, without an extra async state machine per attempt.
    private Task<T> RunAttemptAsync(FetchCycle cycle, int attempt, CancellationToken token) =>
        _runtime.Limiter is null && _runtime.RateLimiter is null && cycle.Options.Timeout is null
            ? Invoke(cycle, attempt, token)
            : RunScheduledAttemptAsync(cycle, attempt, token);

    private async Task<T> RunScheduledAttemptAsync(FetchCycle cycle, int attempt, CancellationToken token)
    {
        using var slot = _runtime.Limiter is { } limiter
            ? await limiter.AcquireAsync(cycle.Options.Priority, token).ConfigureAwait(false)
            : null;
        using var permit = _runtime.RateLimiter is { } rateLimiter
            ? await rateLimiter.AcquireAsync(Key, token).ConfigureAwait(false)
            : null;

        if (cycle.Options.Timeout is not { } timeout)
        {
            return await Invoke(cycle, attempt, token).ConfigureAwait(false);
        }

        using var timeoutSource = new CancellationTokenSource(timeout, _runtime.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutSource.Token);
        var task = Invoke(cycle, attempt, linked.Token);
        try
        {
            // WaitAsync also abandons a fetcher that ignores its token.
            return await task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !token.IsCancellationRequested)
        {
            _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw new TimeoutException($"Fetching {Key} did not complete within {timeout}.");
        }
    }

    private Task<T> Invoke(FetchCycle cycle, int attempt, CancellationToken token)
    {
        var interceptors = _runtime.Interceptors;
        if (interceptors.Length == 0)
        {
            return cycle.Fetcher(token);
        }

        var context = new QueryInterceptorContext(Key, attempt);
        var next = cycle.Fetcher;
        for (var i = interceptors.Length - 1; i >= 0; i--)
        {
            var interceptor = interceptors[i];
            var inner = next;
            next = ct => interceptor.InterceptAsync(context, inner, ct);
        }

        return next(token);
    }

    // Recomputes an adaptive polling interval after a fetch settled (user code, so outside the lock).
    private void UpdateAdaptiveInterval(ResolvedQueryOptions options)
    {
        if (options.AdaptiveRefetchInterval is not { } compute)
        {
            return;
        }

        TimeSpan? next;
        try
        {
            next = compute(Describe());
        }
        catch (Exception)
        {
            next = null;
        }

        lock (_gate)
        {
            _adaptiveInterval = next is { } interval && interval > TimeSpan.Zero ? interval : null;
            UpdateRefetchScheduleLocked();
        }
    }

    // Structural sharing for direct writes (SetQueryData, updaters, hydration, synchronized data), as for fetched
    // data: an equal value keeps the cached instance and a partly changed list or dictionary reuses its unchanged
    // elements. Equals is user code, so it runs outside the lock; if the entry changes meanwhile, the shared value is
    // still equal to the written one, so storing it is as correct as storing the original.
    private T ShareWithCurrent(T data)
    {
        T? current;
        lock (_gate)
        {
            if (!SharesStructureLocked() || !_state.HasData)
            {
                return data;
            }

            current = _state.Data;
        }

        return StructuralSharing.Share(current, data);
    }

    // The query's own setting once it has been fetched, else the client default.
    private bool SharesStructureLocked() =>
        _lastFetcher is not null ? _lastOptions.StructuralSharing : _runtime.Options?.DefaultStructuralSharing ?? false;

    private T? CurrentData()
    {
        lock (_gate)
        {
            return _state.HasData ? _state.Data : default;
        }
    }

    private void ReportRetry(FetchCycle cycle, int attempts, Exception error)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_cycle, cycle))
            {
                SetStateLocked(_state with { FailureCount = attempts }, dataChanged: false);
            }
        }

        Drain();
        _runtime.EventSink?.QueryFetchRetried(Key, attempts, error);
    }

    private void Complete(FetchCycle cycle, FetchOutcome outcome, T data, Exception? error, int attempts)
    {
        lock (_gate)
        {
            cycle.Outcome = outcome;
            cycle.Error = error;
            if (ReferenceEquals(_cycle, cycle))
            {
                _cycle = null;
            }

            if (ReferenceEquals(_detachedCycle, cycle))
            {
                _detachedCycle = null; // settled before any follow-up fetch: do not link a later, unrelated one
            }

            // A superseded cycle must not touch state (its successor owns the outcome), nor a detached one (reset).
            if (cycle.Successor is null && !cycle.Detached)
            {
                var now = Now;
                switch (outcome)
                {
                    case FetchOutcome.Succeeded:
                        _lastActivityAt = now;
                        SetStateLocked(
                            _state with
                            {
                                Status = QueryStatus.Success,
                                Data = data,
                                Error = null,
                                DataUpdatedAt = now,
                                FailureCount = 0,
                            },
                            dataChanged: true);
                        break;

                    case FetchOutcome.Failed:
                        _lastActivityAt = now;
                        SetStateLocked(
                            _state with
                            {
                                Status = QueryStatus.Error,
                                Error = error,
                                ErrorUpdatedAt = now,
                                FailureCount = attempts,
                            },
                            dataChanged: false);
                        break;

                    case FetchOutcome.Paused:
                        _pausedFetchPending = true;
                        _invalidated |= cycle.RevertInvalidated;
                        SetStateLocked(
                            _state with
                            {
                                Status = _state.HasData ? RevertedStatus(cycle.RevertStatus) : QueryStatus.Paused,
                                FailureCount = attempts,
                            },
                            dataChanged: false);
                        break;

                    default:
                        _invalidated |= cycle.RevertInvalidated;
                        SetStateLocked(
                            _state with
                            {
                                Status = _pausedFetchPending && !_state.HasData ? QueryStatus.Paused : RevertedStatus(cycle.RevertStatus),
                                FailureCount = 0,
                            },
                            dataChanged: false);
                        break;
                }
            }
        }

        Drain();
    }

    // Must hold _gate. Maps the pre-fetch status back, accounting for data written while the fetch ran.
    private QueryStatus RevertedStatus(QueryStatus revert) => revert switch
    {
        QueryStatus.Loading or QueryStatus.Refetching => _state.HasData ? QueryStatus.Success : QueryStatus.Idle,
        QueryStatus.Idle or QueryStatus.Paused when _state.HasData => QueryStatus.Success,
        _ => revert,
    };

    private async Task<QueryState<T>> AwaitCycleAsync(FetchCycle cycle, bool throwOnError, CancellationToken cancellationToken)
    {
        // Follow the supersession chain so a caller whose fetch was replaced (e.g. by an invalidation)
        // receives the outcome of the newest fetch rather than the cancelled one.
        while (true)
        {
            await cycle.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);

            FetchCycle? successor;
            lock (_gate)
            {
                // A detached cycle (the entry was reset mid-fetch) has no successor of its own; hand its waiters
                // to the fetch running now, if any (for example the refetch a reset of an observed query starts).
                successor = cycle.Successor ?? (cycle.Detached && _cycle is { } current && !ReferenceEquals(current, cycle) ? current : null);
            }

            if (successor is null)
            {
                break;
            }

            cycle = successor;
        }

        if (throwOnError && cycle.Outcome is FetchOutcome.Failed && cycle.Error is { } error)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }

        return CurrentState;
    }

    // Must hold _gate.
    private bool IsStaleLocked(TimeSpan staleTime)
    {
        if (_invalidated || !_state.HasData || _state.Status is QueryStatus.Error || _state.DataUpdatedAt is not { } updatedAt)
        {
            return true;
        }

        return Now - updatedAt >= staleTime;
    }

    // ---------------------------------------------------------------- imperative data

    /// <summary>Sets the entry's data, marking it fresh. Returns <see langword="false"/> if the entry was removed.</summary>
    public bool TrySetData(T data, DateTimeOffset? updatedAt = null)
    {
        data = ShareWithCurrent(data);
        lock (_gate)
        {
            if (_removed)
            {
                return false;
            }

            ApplyDataLocked(data, updatedAt ?? Now);
        }

        Drain();
        return true;
    }

    /// <summary>
    /// Atomically replaces the entry's data with <paramref name="updater"/>(current data). The updater runs
    /// outside the lock; if the entry changes concurrently it is re-run against the newer data (optimistic
    /// concurrency), so no update is ever lost. Returns <see langword="false"/> if the entry was removed.
    /// </summary>
    public bool TryUpdateData(Func<T?, T> updater, out T result)
    {
        var write = TryUpdateDataCore(updater);
        result = write is { } w ? w.Result : default!;
        return write is not null;
    }

    /// <summary>
    /// The update primitive behind <see cref="TryUpdateData(Func{T, T}, out T)"/>. Returns the state the update
    /// was computed from (for an exact rollback) and the entry version right after the write, or
    /// <see langword="null"/> if the entry was removed.
    /// </summary>
    internal DataWrite? TryUpdateDataCore(Func<T?, T> updater)
    {
        while (true)
        {
            QueryState<T> current;
            bool invalidated;
            long observed;
            bool share;
            lock (_gate)
            {
                if (_removed)
                {
                    return null;
                }

                current = _state;
                invalidated = _invalidated;
                observed = _version;
                share = SharesStructureLocked();
            }

            var candidate = updater(current.HasData ? current.Data : default);
            if (share && current.HasData)
            {
                candidate = StructuralSharing.Share(current.Data, candidate);
            }

            DataWrite write;
            lock (_gate)
            {
                if (_removed)
                {
                    return null;
                }

                if (_version != observed)
                {
                    continue; // changed while the updater ran: recompute from the newer state
                }

                ApplyDataLocked(candidate, Now);
                write = new DataWrite(candidate, current, invalidated, _version);
            }

            Drain();
            return write;
        }
    }

    /// <summary>The outcome of <see cref="TryUpdateDataCore"/>.</summary>
    internal readonly record struct DataWrite(T Result, QueryState<T> Previous, bool PreviousInvalidated, long Version);

    /// <summary>
    /// Restores a previous state exactly (data, timestamps, status and invalidation), but only if the
    /// entry has not changed since <paramref name="expectedVersion"/>. Returns <see langword="false"/> when it
    /// changed in between (another write landed on top), in which case nothing is restored.
    /// </summary>
    internal bool TryRestore(QueryState<T> state, bool invalidated, long expectedVersion)
    {
        lock (_gate)
        {
            if (!TryRestoreLocked(state, invalidated, expectedVersion))
            {
                return false;
            }
        }

        Drain();
        return true;
    }

    /// <summary>The body of <see cref="TryRestore"/>; the caller holds <see cref="Gate"/> and drains afterwards.</summary>
    internal bool TryRestoreLocked(QueryState<T> state, bool invalidated, long expectedVersion)
    {
        if (_removed || _version != expectedVersion)
        {
            return false;
        }

        _invalidated = invalidated;
        SetStateLocked(
            state with
            {
                Status = state.Status is QueryStatus.Loading or QueryStatus.Refetching
                    ? (state.HasData ? QueryStatus.Success : QueryStatus.Idle)
                    : state.Status,
            },
            dataChanged: true);
        return true;
    }

    /// <summary>
    /// Reads what a write needs to compute and later roll back: the state, the invalidation flag and the version.
    /// Returns <see langword="null"/> if the entry was removed.
    /// </summary>
    internal (QueryState<T> State, bool Invalidated, long Version)? SnapshotForWrite()
    {
        lock (_gate)
        {
            return _removed ? null : (_state, _invalidated, _version);
        }
    }

    /// <summary>Whether the entry is live and still at <paramref name="version"/>. The caller holds <see cref="Gate"/>.</summary>
    internal bool IsAtVersionLocked(long version) => !_removed && _version == version;

    /// <summary>Whether the entry was removed. The caller holds <see cref="Gate"/>.</summary>
    internal bool IsRemovedLocked => _removed;

    /// <summary>Writes data (marking it fresh) and returns the new version. The caller holds <see cref="Gate"/> and drains afterwards.</summary>
    internal long WriteDataLocked(T data)
    {
        ApplyDataLocked(data, Now);
        return _version;
    }

    /// <summary>Delivers notifications queued by <c>*Locked</c> writes made under <see cref="Gate"/>.</summary>
    internal void FlushNotifications() => Drain();

    /// <summary>
    /// Applies externally produced data (server prerender, persisted cache) only when it is newer than what
    /// the entry already holds. Returns <see langword="null"/> if removed, otherwise whether it was applied.
    /// </summary>
    public bool? TryHydrate(T data, DateTimeOffset updatedAt)
    {
        data = ShareWithCurrent(data);
        lock (_gate)
        {
            if (_removed)
            {
                return null;
            }

            if (_state.DataUpdatedAt is { } existing && existing >= updatedAt)
            {
                return false;
            }

            ApplyDataLocked(data, updatedAt);
        }

        Drain();
        return true;
    }

    /// <summary>
    /// Writes data received from another cache with its original timestamp - atomically, only if it is not older
    /// than the data held now, unless <paramref name="force"/> is set. Returns <see langword="null"/> if removed.
    /// </summary>
    internal bool? TryApplyRemote(T data, DateTimeOffset updatedAt, bool force)
    {
        data = ShareWithCurrent(data);
        lock (_gate)
        {
            if (_removed)
            {
                return null;
            }

            if (!force && _state.HasData && _state.DataUpdatedAt is { } existing && existing > updatedAt)
            {
                return false;
            }

            ApplyDataLocked(data, updatedAt);
        }

        Drain();
        return true;
    }

    // Must hold _gate.
    private void ApplyDataLocked(T data, DateTimeOffset updatedAt)
    {
        _invalidated = false;
        _lastActivityAt = Now;
        SetStateLocked(
            _state with
            {
                // A fetch may still be running (it will overwrite this unless cancelled first).
                Status = _cycle is null ? QueryStatus.Success : QueryStatus.Refetching,
                Data = data,
                Error = null,
                DataUpdatedAt = updatedAt,
                FailureCount = 0,
            },
            dataChanged: true);
    }

    // ---------------------------------------------------------------- observers

    /// <summary>
    /// Subscribes a freshly created observer (with no handlers attached yet), seeding it with the current
    /// state. Returns <see langword="false"/> if the entry was removed.
    /// </summary>
    internal bool TryAddObserver(IQuerySubscriber<T> observer)
    {
        QueryState<T> current;
        long version;
        lock (_gate)
        {
            if (_removed)
            {
                return false;
            }

            _observers.Add(observer);
            _lastActivityAt = Now;
            current = _state;
            version = _version;
            UpdateRefetchScheduleLocked();

            // Seed the snapshot while holding the lock: every later change reaches the observer through the
            // ordered outbox with a higher version, so the seed can never overtake a newer notification.
            observer.Seed(current, version);
        }

        return true;
    }

    internal void RemoveObserver(IQuerySubscriber<T> observer)
    {
        CancellationTokenSource? cancel = null;
        lock (_gate)
        {
            if (!_observers.Remove(observer))
            {
                return;
            }

            _lastActivityAt = Now;
            UpdateRefetchScheduleLocked();

            // When the last observer leaves mid-fetch and the query opts in, abort the shared fetch so
            // work for a no-longer-visible query stops. The previous state is restored.
            if (_observers.Count == 0 && _cycle is not null && _lastOptions.CancelInFlightWhenUnobserved)
            {
                cancel = _cycle.Cts;
            }
        }

        CancelQuietly(cancel);
    }

    /// <summary>
    /// Starts, updates, or stops the periodic background refetch timer based on whether the entry has
    /// active observers and a configured interval. Must be called while holding <see cref="_gate"/>.
    /// </summary>
    private void UpdateRefetchScheduleLocked()
    {
        var interval = _observers.Count > 0 && !_removed && _lastFetcher is not null && _lastOptions.Enabled
            ? (_lastOptions.AdaptiveRefetchInterval is not null ? _adaptiveInterval : _lastOptions.RefetchInterval)
            : null;

        if (interval is { } period)
        {
            if (_refreshTimer is null)
            {
                _refreshTimer = _runtime.TimeProvider.CreateTimer(
                    static state => ((QueryEntry<T>)state!).OnRefetchInterval(),
                    this,
                    period,
                    period);
            }
            else if (_refreshInterval != period)
            {
                _refreshTimer.Change(period, period);
            }

            _refreshInterval = period;
        }
        else
        {
            _refreshTimer?.Dispose();
            _refreshTimer = null;
        }
    }

    private void OnRefetchInterval() => _ = RefetchAsync(FetchMode.Force, includeDisabled: false, CancellationToken.None);

    // ---------------------------------------------------------------- lifecycle

    public bool TryEvict()
    {
        lock (_gate)
        {
            if (_removed || _observers.Count > 0 || _cycle is not null || _pausedFetchPending)
            {
                return false;
            }

            _removed = true;
            _refreshTimer?.Dispose();
            _refreshTimer = null;
            return true;
        }
    }

    public EntrySnapshot? CaptureSnapshot()
    {
        lock (_gate)
        {
            return _removed ? null : new EntrySnapshot<T>(Key, _state, _invalidated);
        }
    }

    /// <summary>
    /// Restores a captured state exactly (a fetch status becomes the settled status it implies), detaching any fetch in
    /// flight. Returns <see langword="null"/> if the entry was removed.
    /// </summary>
    internal bool? TryRestoreSnapshot(QueryState<T> state, bool invalidated)
    {
        CancellationTokenSource? cancel = null;
        lock (_gate)
        {
            if (_removed)
            {
                return null;
            }

            if (_cycle is { } cycle)
            {
                cycle.Detached = true;
                cancel = cycle.Cts;
                _cycle = null;
                _detachedCycle = cycle;
            }

            _invalidated = invalidated;
            _pausedFetchPending = false;
            SetStateLocked(
                state with
                {
                    Status = state.Status is QueryStatus.Loading or QueryStatus.Refetching
                        ? (state.HasData ? QueryStatus.Success : QueryStatus.Idle)
                        : state.Status,
                },
                dataChanged: true);
        }

        CancelQuietly(cancel);
        Drain();
        return true;
    }

    /// <summary>Atomically marks the entry removed if it is inactive, idle and past its retention window.</summary>
    public bool TryCollect(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_removed || _observers.Count > 0 || _cycle is not null || _pausedFetchPending || now - _lastActivityAt < _cacheTime)
            {
                return false;
            }

            _removed = true;
            _refreshTimer?.Dispose();
            _refreshTimer = null;
            return true;
        }
    }

    /// <summary>Marks the entry removed and cancels any in-flight fetch.</summary>
    public void Dispose()
    {
        CancellationTokenSource? cancel;
        lock (_gate)
        {
            _removed = true;
            _refreshTimer?.Dispose();
            _refreshTimer = null;
            cancel = _cycle?.Cts;
        }

        CancelQuietly(cancel);
    }

    public QueryDescriptor Describe()
    {
        lock (_gate)
        {
            return new QueryDescriptor
            {
                Key = Key,
                Status = _state.Status,
                DataTypeName = TypeKeyName<T>.Value,
                HasData = _state.HasData,
                Data = _state.HasData ? _state.Data : null,
                IsStale = IsStaleLocked(_lastFetcher is null ? TimeSpan.Zero : _lastOptions.StaleTime),
                ObserverCount = _observers.Count,
                IsActive = _observers.Count > 0,
                FailureCount = _state.FailureCount,
                FetchCount = _fetchCount,
                ErrorMessage = _state.Error?.Message,
                DataUpdatedAt = _state.DataUpdatedAt,
                ErrorUpdatedAt = _state.ErrorUpdatedAt,
                LastActivityAt = _lastActivityAt,
                CacheTime = _cacheTime,
                StaleTime = _lastFetcher is null ? null : _lastOptions.StaleTime,
                Meta = _lastFetcher is null ? null : _lastOptions.Meta,
            };
        }
    }

    // ---------------------------------------------------------------- notification plumbing

    // Must hold _gate. Records the new state and queues its notification; call Drain() after releasing the lock.
    private void SetStateLocked(QueryState<T> state, bool dataChanged)
    {
        if (!dataChanged && SameState(state, _state))
        {
            return;
        }

        _state = state;
        _version++;
        _outbox.Enqueue(new Pending(
            state,
            _version,
            _observers.Count == 0 ? [] : [.. _observers],
            dataChanged ? QueryCacheChangeKind.DataUpdated : QueryCacheChangeKind.StateChanged));
    }

    // Compares two states without calling the data's Equals (user code must not run under the lock). Callers that
    // pass dataChanged: false never change Data, so reference identity is enough for it.
    private static bool SameState(QueryState<T> a, QueryState<T> b) =>
        a.Status == b.Status
        && ReferenceEquals(a.Error, b.Error)
        && a.DataUpdatedAt == b.DataUpdatedAt
        && a.ErrorUpdatedAt == b.ErrorUpdatedAt
        && a.FailureCount == b.FailureCount
        && (typeof(T).IsValueType || ReferenceEquals(a.Data, b.Data));

    /// <summary>
    /// Delivers queued notifications in order. Only one thread drains at a time; a thread that finds a
    /// drain in progress leaves its notification to that thread, which keeps delivery strictly ordered
    /// (including notifications raised re-entrantly from a handler).
    /// </summary>
    private void Drain()
    {
        // Inside QueryNotifications.Batch(), deliver at the end of the batch instead.
        if (NotificationBatchScope.TryDefer(this, _drainNow ??= DrainNow))
        {
            return;
        }

        DrainNow();
    }

    private void DrainNow()
    {
        lock (_gate)
        {
            if (_draining || _outbox.Count == 0)
            {
                return;
            }

            _draining = true;
        }

        while (true)
        {
            Pending next;
            lock (_gate)
            {
                if (!_outbox.TryDequeue(out next))
                {
                    _draining = false;
                    return;
                }
            }

            foreach (var observer in next.Observers)
            {
                observer.NotifyStateChanged(next.State, next.Version);
            }

            _runtime.RaiseChanged(Key, next.Kind);
        }
    }

    private static void CancelQuietly(CancellationTokenSource? source)
    {
        if (source is null)
        {
            return;
        }

        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The cycle already finished and released its source.
        }
        catch (AggregateException)
        {
            // A token callback registered by user code threw; cancellation still took effect.
        }
    }

    private readonly struct Pending(QueryState<T> state, long version, IQuerySubscriber<T>[] observers, QueryCacheChangeKind kind)
    {
        public QueryState<T> State { get; } = state;

        public long Version { get; } = version;

        public IQuerySubscriber<T>[] Observers { get; } = observers;

        public QueryCacheChangeKind Kind { get; } = kind;
    }

    private sealed class FetchCycle(Func<CancellationToken, Task<T>> fetcher, ResolvedQueryOptions options)
    {
        public Func<CancellationToken, Task<T>> Fetcher { get; } = fetcher;

        public ResolvedQueryOptions Options { get; } = options;

        public CancellationTokenSource Cts { get; } = new();

        public Task Completion { get; set; } = Task.CompletedTask;

        /// <summary>The cycle that replaced this one; non-null means this cycle was superseded.</summary>
        public FetchCycle? Successor { get; set; }

        /// <summary>Set when the entry was reset mid-fetch: the cycle's outcome must not touch state.</summary>
        public bool Detached { get; set; }

        public QueryStatus RevertStatus { get; init; }

        public bool RevertInvalidated { get; init; }

        public FetchOutcome Outcome { get; set; }

        public Exception? Error { get; set; }
    }

    private enum FetchOutcome
    {
        Succeeded,
        Failed,
        Cancelled,
        Paused,
    }
}
