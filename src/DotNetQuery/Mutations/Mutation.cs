using System.Runtime.ExceptionServices;

namespace DotNetQuery;

/// <summary>
/// A reusable, observable mutation. Invoke <see cref="MutateAsync"/> to run the underlying operation
/// with optimistic updates, rollback, retries, invalidation and lifecycle callbacks.
/// </summary>
/// <remarks>
/// <para>
/// A single instance exposes one observable state that always tracks its most recent run: if
/// <see cref="MutateAsync"/> is called again while a previous run is still pending, the older run completes
/// normally (its callbacks fire and its task returns its own result) but no longer updates
/// <see cref="CurrentState"/>. For independent parallel mutations whose states you want to observe
/// separately, use one instance per operation or the one-shot <see cref="IQueryClient.MutateAsync"/>.
/// </para>
/// <para>
/// Run order: <see cref="MutationOptions{TVariables, TData}.OptimisticUpdates"/> →
/// <see cref="MutationOptions{TVariables, TData}.OnMutate"/> → the mutation function (with retries) →
/// <see cref="MutationOptions{TVariables, TData}.OnSuccess"/> or (after rolling back optimistic updates)
/// <see cref="MutationOptions{TVariables, TData}.OnError"/> → <see cref="MutationOptions{TVariables, TData}.OnSettled"/> →
/// <see cref="MutationOptions{TVariables, TData}.Invalidates"/>. The mutation stays pending until all of
/// them complete. A callback that throws turns the run into a failure, but the mutation function itself is
/// never re-run because of it.
/// </para>
/// </remarks>
/// <typeparam name="TVariables">The type of the variables passed to the mutation.</typeparam>
/// <typeparam name="TData">The type of data returned by the mutation.</typeparam>
public sealed class Mutation<TVariables, TData>
{
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    private readonly Func<TVariables, CancellationToken, Task<TData>> _mutationFn;
    private readonly MutationOptions<TVariables, TData> _options;
    private readonly TimeProvider _timeProvider;
    private readonly IQueryEventSink? _eventSink;
    private readonly IQueryClient? _client;
    private readonly MutationCache? _mutationCache;
    private readonly QueryRuntime? _runtime;
    private readonly NetworkMode _defaultNetworkMode;
    private readonly object _gate = new();
    private readonly Queue<MutationState<TVariables, TData>> _outbox = new();
    private MutationState<TVariables, TData> _state = MutationState<TVariables, TData>.Idle;
    private long _latestRun;
    private bool _draining;
    private readonly List<(TVariables Variables, Task<MutationResult<TVariables, TData>> Run)> _pendingRuns = [];

    /// <summary>The progress-reporting mutation function, when the mutation was created with one.</summary>
    internal Func<TVariables, IProgress<double>, CancellationToken, Task<TData>>? ProgressFn { get; init; }

    internal Mutation(
        Func<TVariables, CancellationToken, Task<TData>> mutationFn,
        MutationOptions<TVariables, TData>? options,
        TimeProvider timeProvider,
        IQueryEventSink? eventSink = null,
        IQueryClient? client = null,
        MutationCache? mutationCache = null,
        QueryRuntime? runtime = null,
        NetworkMode defaultNetworkMode = NetworkMode.Always)
    {
        _runtime = runtime;
        _defaultNetworkMode = defaultNetworkMode;
        _mutationFn = mutationFn;
        _options = options ?? new MutationOptions<TVariables, TData>();
        _timeProvider = timeProvider;
        _eventSink = eventSink;
        _client = client;
        _mutationCache = mutationCache;
    }

    /// <summary>Raised whenever the mutation's state changes.</summary>
    public event Action<MutationState<TVariables, TData>>? StateChanged;

    /// <summary>Gets an immutable snapshot of the mutation's current state.</summary>
    public MutationState<TVariables, TData> CurrentState
    {
        get { lock (_gate) { return _state; } }
    }

    /// <summary>Gets the current state as a <see cref="MutationResult{TVariables, TData}"/>.</summary>
    public MutationResult<TVariables, TData> CurrentResult => new(CurrentState);

    /// <summary>Gets the options this mutation was created with.</summary>
    public MutationOptions<TVariables, TData> Options => _options;

    /// <summary>
    /// Runs the mutation with the supplied variables.
    /// </summary>
    /// <param name="variables">The input to the mutation.</param>
    /// <param name="cancellationToken">
    /// Cancels the run. A cancelled run still rolls back its optimistic updates and runs
    /// <see cref="MutationOptions{TVariables, TData}.OnError"/>/<see cref="MutationOptions{TVariables, TData}.OnSettled"/>
    /// (with <see cref="CancellationToken.None"/>), then throws <see cref="OperationCanceledException"/>.
    /// </param>
    /// <returns>
    /// The settled result of this run. Throws the captured error if
    /// <see cref="MutationOptions{TVariables, TData}.ThrowOnError"/> is set.
    /// </returns>
    public Task<MutationResult<TVariables, TData>> MutateAsync(TVariables variables, CancellationToken cancellationToken = default)
    {
        if (!_options.DeduplicatePending)
        {
            return RunAsync(variables, cancellationToken);
        }

        lock (_pendingRuns)
        {
            foreach (var (pendingVariables, pendingRun) in _pendingRuns)
            {
                if (EqualityComparer<TVariables>.Default.Equals(pendingVariables, variables))
                {
                    return pendingRun.WaitAsync(cancellationToken);
                }
            }

            var started = RunAsync(variables, CancellationToken.None);
            _pendingRuns.Add((variables, started));
            _ = started.ContinueWith(
                (task, state) =>
                {
                    var list = (List<(TVariables, Task<MutationResult<TVariables, TData>>)>)state!;
                    lock (list)
                    {
                        list.RemoveAll(p => ReferenceEquals(p.Item2, task));
                    }
                },
                _pendingRuns,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return started.WaitAsync(cancellationToken);
        }
    }

    private async Task<MutationResult<TVariables, TData>> RunAsync(TVariables variables, CancellationToken cancellationToken)
    {
        var run = Interlocked.Increment(ref _latestRun);
        var submittedAt = _timeProvider.GetUtcNow();
        var state = new MutationState<TVariables, TData>
        {
            Status = MutationStatus.Pending,
            Variables = variables,
            SubmittedAt = submittedAt,
        };
        Publish(run, state);

        var (mutationId, sinkToken) = _eventSink?.MutationStarted(_options.MutationKey, variables) ?? (0, null);
        var tracked = _mutationCache?.Add(mutationId, _options.MutationKey, _options.Scope, variables, submittedAt);
        var started = _timeProvider.GetTimestamp();

        object? context = null;
        QueryDataCommit? optimistic = null;
        IDisposable? scopeLease = null;
        var failures = 0;
        var committed = false;
        var settledCalled = false;
        TData data = default!;

        try
        {
            try
            {
                if (_options.OptimisticUpdates is { Count: > 0 } updates && _client is not null)
                {
                    optimistic = await ApplyOptimisticUpdatesAsync(_client, updates, variables, cancellationToken).ConfigureAwait(false);
                    _eventSink?.OptimisticApplied(mutationId, optimistic.Keys);
                }

                if (_options.OnMutate is not null)
                {
                    context = await _options.OnMutate(variables, cancellationToken).ConfigureAwait(false);
                }

                if (_options.Scope is { } scope && _mutationCache is not null)
                {
                    scopeLease = await _mutationCache.EnterScopeAsync(scope, () => Report(state = state with { IsPaused = true }), cancellationToken).ConfigureAwait(false);
                    if (state.IsPaused)
                    {
                        Report(state = state with { IsPaused = false });
                    }
                }

                var networkMode = _options.NetworkMode ?? _defaultNetworkMode;
                if (networkMode is NetworkMode.Online)
                {
                    await WaitForConnectivityAsync().ConfigureAwait(false);
                }

                while (true)
                {
                    try
                    {
                        data = ProgressFn is { } withProgress
                            ? await withProgress(variables, new ActionProgress(value => Report(state = state with { Progress = value })), cancellationToken).ConfigureAwait(false)
                            : await _mutationFn(variables, cancellationToken).ConfigureAwait(false);
                        committed = true;
                        break;
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested && failures < _options.RetryCount && ShouldRetry(failures + 1, ex))
                    {
                        failures++;
                        state = state with { FailureCount = failures, Error = ex };
                        Report(state);

                        var delay = (_options.RetryDelay ?? RetryPolicy.Constant(TimeSpan.Zero))(failures - 1, ex);
                        if (delay > TimeSpan.Zero)
                        {
                            await Task.Delay(delay < MaxRetryDelay ? delay : MaxRetryDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
                        }

                        if (networkMode is not NetworkMode.Always)
                        {
                            await WaitForConnectivityAsync().ConfigureAwait(false);
                        }
                    }
                }

                // From here on the server has accepted the write. A throwing callback still fails the run (so
                // the caller learns about it), but the write is never undone or retried because of it.
                if (_options.OnSuccess is not null)
                {
                    await _options.OnSuccess(data, variables, context, CallbackToken()).ConfigureAwait(false);
                }

                if (_options.OnSettled is not null)
                {
                    settledCalled = true;
                    await _options.OnSettled(data, null, variables, context, CallbackToken()).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                return await FailAsync(ex).ConfigureAwait(false);
            }

            optimistic?.Complete();
            await ReconcileAsync().ConfigureAwait(false);

            state = state with { Status = MutationStatus.Success, Data = data, Error = null, FailureCount = failures };
            Report(state);
            _eventSink?.MutationCompleted(mutationId, _options.MutationKey, true, null, failures, _timeProvider.GetElapsedTime(started), sinkToken);
            return new MutationResult<TVariables, TData>(state);
        }
        finally
        {
            // Release the scope only once this run fully settled (callbacks and invalidation included).
            scopeLease?.Dispose();
        }

        // Waits (pending + paused) while the connectivity provider reports offline.
        async Task WaitForConnectivityAsync()
        {
            if (_runtime is null || _runtime.IsOnline)
            {
                return;
            }

            Report(state = state with { IsPaused = true });
            await _runtime.WhenOnlineAsync(cancellationToken).ConfigureAwait(false);
            Report(state = state with { IsPaused = false });
        }

        void Report(MutationState<TVariables, TData> next)
        {
            Publish(run, next);
            tracked?.Update(next.Status, next.Data, next.Error, next.FailureCount, next.IsPaused, next.Progress);
        }

        // After the write is committed, cancelling the caller's wait must not cancel follow-up work.
        CancellationToken CallbackToken() => committed ? CancellationToken.None : cancellationToken;

        // Keeps the mutation pending until dependent active queries have refetched. Never fails the run:
        // the caller may stop waiting (cancellation), but the refetches themselves continue.
        async Task ReconcileAsync()
        {
            try
            {
                await InvalidateAsync(CancellationToken.None).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Invalidation failures are captured on the queries themselves.
            }
        }

        async Task<MutationResult<TVariables, TData>> FailAsync(Exception error)
        {
            var cancelled = !committed && error is OperationCanceledException && cancellationToken.IsCancellationRequested;
            if (!cancelled && !committed)
            {
                failures++;
            }

            // Settle with a live token: rollback and invalidation must still happen after cancellation.
            var settleToken = cancelled || committed ? CancellationToken.None : cancellationToken;

            // Roll back optimistic updates only if the server did not accept the write.
            if (optimistic is not null && !committed)
            {
                await RunQuietlyAsync(() => RollBackAsync(_client!, optimistic, mutationId)).ConfigureAwait(false);
            }
            else
            {
                optimistic?.Complete();
            }

            if (_options.OnError is not null)
            {
                await RunQuietlyAsync(() => _options.OnError(error, variables, context, settleToken).AsTask()).ConfigureAwait(false);
            }

            if (_options.OnSettled is not null && !settledCalled)
            {
                await RunQuietlyAsync(() => _options.OnSettled(default, error, variables, context, settleToken).AsTask()).ConfigureAwait(false);
            }

            await RunQuietlyAsync(() => InvalidateAsync(CancellationToken.None)).ConfigureAwait(false);

            state = state with { Status = MutationStatus.Error, Error = error, FailureCount = failures, IsPaused = false };
            Report(state);
            _eventSink?.MutationCompleted(mutationId, _options.MutationKey, false, error, failures, _timeProvider.GetElapsedTime(started), sinkToken);

            if (cancelled || _options.ThrowOnError)
            {
                ExceptionDispatchInfo.Capture(error).Throw();
            }

            return new MutationResult<TVariables, TData>(state);
        }
    }

    /// <summary>Resets the mutation back to its idle state.</summary>
    public void Reset()
    {
        var run = Interlocked.Increment(ref _latestRun);
        Publish(run, MutationState<TVariables, TData>.Idle);
    }

    // Cancels in-flight fetches for every target key (so a pending refetch cannot overwrite the optimistic values
    // when it lands), then writes all optimistic values in one atomic transaction: observers never see some keys
    // updated and others not, and an updater that throws leaves the cache untouched.
    private static async Task<QueryDataCommit> ApplyOptimisticUpdatesAsync(
        IQueryClient client,
        IReadOnlyList<OptimisticUpdate<TVariables>> updates,
        TVariables variables,
        CancellationToken cancellationToken)
    {
        var cancellations = new Task[updates.Count];
        for (var i = 0; i < updates.Count; i++)
        {
            var key = updates[i].Key;
            cancellations[i] = client.CancelQueriesAsync(k => k == key, cancellationToken);
        }

        await Task.WhenAll(cancellations).ConfigureAwait(false);
        var batch = new QueryDataBatch();
        foreach (var update in updates)
        {
            update.AddTo(batch, variables);
        }

        // Optimistic commits join each entry's optimistic layers, so overlapping mutations roll back independently.
        return QueryDataTransaction.Commit(client.Cache, batch.Writes, optimistic: true);
    }

    // Restores the exact pre-mutation snapshots atomically. A key that something else wrote on top of meanwhile
    // (another mutation's optimistic update, a refetch) is not restored - that would clobber the newer write - but
    // invalidated, so it is reconciled with the server instead.
    private Task RollBackAsync(IQueryClient client, QueryDataCommit optimistic, long mutationId)
    {
        var conflicts = optimistic.Rollback();
        _eventSink?.OptimisticRolledBack(mutationId, optimistic.Keys.Except(conflicts).ToArray(), conflicts);
        if (conflicts.Count == 0)
        {
            return Task.CompletedTask;
        }

        return Task.WhenAll(conflicts.Select(key => client.InvalidateQueriesAsync(k => k == key, CancellationToken.None)));
    }

    private async Task InvalidateAsync(CancellationToken cancellationToken)
    {
        if (_options.Invalidates is not { Count: > 0 } keys || _client is null)
        {
            return;
        }

        var invalidations = new Task[keys.Count];
        for (var i = 0; i < keys.Count; i++)
        {
            invalidations[i] = _client.InvalidateQueriesAsync(keys[i], cancellationToken);
        }

        await Task.WhenAll(invalidations).ConfigureAwait(false);
    }

    // Reports progress synchronously on the reporting thread (System.Progress would post to a captured context).
    private sealed class ActionProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    // A predicate that throws vetoes the retry, so the run fails with the mutation's own error.
    private bool ShouldRetry(int failureCount, Exception error)
    {
        if (_options.ShouldRetry is not { } predicate)
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

    private static async Task RunQuietlyAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Settle-phase callbacks must not mask the original failure or skip the remaining cleanup.
        }
    }

    // Only the most recent run (or Reset) drives the observable state. Notifications are queued under the
    // lock and delivered by one thread at a time, so subscribers always see them in order and the last one
    // delivered matches CurrentState.
    private void Publish(long run, MutationState<TVariables, TData> state)
    {
        lock (_gate)
        {
            if (run != Volatile.Read(ref _latestRun))
            {
                return;
            }

            _state = state;
            _outbox.Enqueue(state);
            if (_draining)
            {
                return;
            }

            _draining = true;
        }

        while (true)
        {
            MutationState<TVariables, TData> next;
            lock (_gate)
            {
                if (!_outbox.TryDequeue(out next!))
                {
                    _draining = false;
                    return;
                }
            }

            var handlers = StateChanged;
            if (handlers is null)
            {
                continue;
            }

            foreach (var handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<MutationState<TVariables, TData>>)handler)(next);
                }
                catch (Exception)
                {
                    // A faulty subscriber must not fail the mutation.
                }
            }
        }
    }
}
