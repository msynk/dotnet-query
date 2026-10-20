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
    private readonly object _gate = new();
    private readonly Queue<MutationState<TVariables, TData>> _outbox = new();
    private MutationState<TVariables, TData> _state = MutationState<TVariables, TData>.Idle;
    private long _latestRun;
    private bool _draining;

    internal Mutation(
        Func<TVariables, CancellationToken, Task<TData>> mutationFn,
        MutationOptions<TVariables, TData>? options,
        TimeProvider timeProvider,
        IQueryEventSink? eventSink = null,
        IQueryClient? client = null)
    {
        _mutationFn = mutationFn;
        _options = options ?? new MutationOptions<TVariables, TData>();
        _timeProvider = timeProvider;
        _eventSink = eventSink;
        _client = client;
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
    public async Task<MutationResult<TVariables, TData>> MutateAsync(TVariables variables, CancellationToken cancellationToken = default)
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
        var started = _timeProvider.GetTimestamp();

        object? context = null;
        List<Func<Task>>? rollbacks = null;
        var failures = 0;
        var committed = false;
        var settledCalled = false;
        TData data = default!;

        try
        {
            if (_options.OptimisticUpdates is { Count: > 0 } updates && _client is not null)
            {
                rollbacks = new List<Func<Task>>(updates.Count);
                foreach (var update in updates)
                {
                    rollbacks.Add(await update.ApplyAsync(_client, variables, cancellationToken).ConfigureAwait(false));
                }
            }

            if (_options.OnMutate is not null)
            {
                context = await _options.OnMutate(variables, cancellationToken).ConfigureAwait(false);
            }

            while (true)
            {
                try
                {
                    data = await _mutationFn(variables, cancellationToken).ConfigureAwait(false);
                    committed = true;
                    break;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested && failures < _options.RetryCount)
                {
                    failures++;
                    state = state with { FailureCount = failures, Error = ex };
                    Publish(run, state);

                    var delay = (_options.RetryDelay ?? RetryPolicy.Constant(TimeSpan.Zero))(failures - 1, ex);
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay < MaxRetryDelay ? delay : MaxRetryDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
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

        await ReconcileAsync().ConfigureAwait(false);

        state = state with { Status = MutationStatus.Success, Data = data, Error = null, FailureCount = failures };
        Publish(run, state);
        _eventSink?.MutationCompleted(mutationId, _options.MutationKey, true, null, failures, _timeProvider.GetElapsedTime(started), sinkToken);
        return new MutationResult<TVariables, TData>(state);

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
            if (rollbacks is not null && !committed)
            {
                for (var i = rollbacks.Count - 1; i >= 0; i--)
                {
                    await RunQuietlyAsync(rollbacks[i]).ConfigureAwait(false);
                }
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

            state = state with { Status = MutationStatus.Error, Error = error, FailureCount = failures };
            Publish(run, state);
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
