namespace DotNetQuery;

/// <summary>
/// A reusable, observable mutation. Invoke <see cref="MutateAsync"/> to run the underlying operation
/// with optimistic updates, rollback, retries and lifecycle callbacks.
/// </summary>
/// <remarks>
/// A single instance tracks one shared observable state, so it is intended for sequential use (e.g.
/// one form submission at a time) — overlapping concurrent calls on the same instance observe each
/// other's transitions. For independent parallel mutations, use the one-shot
/// <see cref="IQueryClient.MutateAsync"/>, which creates a fresh instance per call.
/// </remarks>
/// <typeparam name="TVariables">The type of the variables passed to the mutation.</typeparam>
/// <typeparam name="TData">The type of data returned by the mutation.</typeparam>
public sealed class Mutation<TVariables, TData>
{
    private readonly Func<TVariables, CancellationToken, Task<TData>> _mutationFn;
    private readonly MutationOptions<TVariables, TData> _options;
    private readonly TimeProvider _timeProvider;
    private readonly IQueryEventSink? _eventSink;
    private readonly object _gate = new();
    private MutationState<TVariables, TData> _state = MutationState<TVariables, TData>.Idle;

    internal Mutation(
        Func<TVariables, CancellationToken, Task<TData>> mutationFn,
        MutationOptions<TVariables, TData>? options,
        TimeProvider timeProvider,
        IQueryEventSink? eventSink = null)
    {
        _mutationFn = mutationFn;
        _options = options ?? new MutationOptions<TVariables, TData>();
        _timeProvider = timeProvider;
        _eventSink = eventSink;
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

    /// <summary>
    /// Runs the mutation with the supplied variables.
    /// </summary>
    /// <param name="variables">The input to the mutation.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The settled result. Throws the captured error if <see cref="MutationOptions{TVariables, TData}.ThrowOnError"/> is set.</returns>
    public async Task<MutationResult<TVariables, TData>> MutateAsync(TVariables variables, CancellationToken cancellationToken = default)
    {
        SetState(_ => new MutationState<TVariables, TData>
        {
            Status = MutationStatus.Pending,
            Variables = variables,
            SubmittedAt = _timeProvider.GetUtcNow(),
            FailureCount = 0,
        });

        _eventSink?.MutationStarted();
        var startTimestamp = _timeProvider.GetTimestamp();

        object? context = null;
        if (_options.OnMutate is not null)
        {
            context = await _options.OnMutate(variables, cancellationToken).ConfigureAwait(false);
        }

        var attempt = 0;
        while (true)
        {
            try
            {
                var data = await _mutationFn(variables, cancellationToken).ConfigureAwait(false);

                SetState(previous => previous with
                {
                    Status = MutationStatus.Success,
                    Data = data,
                    Error = null,
                });

                if (_options.OnSuccess is not null)
                {
                    await _options.OnSuccess(data, variables, context, cancellationToken).ConfigureAwait(false);
                }

                if (_options.OnSettled is not null)
                {
                    await _options.OnSettled(data, null, variables, context, cancellationToken).ConfigureAwait(false);
                }

                _eventSink?.MutationCompleted(true, null, _timeProvider.GetElapsedTime(startTimestamp));
                return CurrentResult;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                attempt++;
                var retryDelay = _options.RetryDelay ?? RetryPolicy.Constant(TimeSpan.Zero);

                if (attempt <= _options.RetryCount)
                {
                    SetState(previous => previous with { FailureCount = attempt });
                    var delay = retryDelay(attempt - 1, ex);
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                SetState(previous => previous with
                {
                    Status = MutationStatus.Error,
                    Error = ex,
                    FailureCount = attempt,
                });

                if (_options.OnError is not null)
                {
                    await _options.OnError(ex, variables, context, cancellationToken).ConfigureAwait(false);
                }

                if (_options.OnSettled is not null)
                {
                    await _options.OnSettled(default, ex, variables, context, cancellationToken).ConfigureAwait(false);
                }

                _eventSink?.MutationCompleted(false, ex, _timeProvider.GetElapsedTime(startTimestamp));

                if (_options.ThrowOnError)
                {
                    throw;
                }

                return CurrentResult;
            }
        }
    }

    /// <summary>Resets the mutation back to its idle state.</summary>
    public void Reset() => SetState(_ => MutationState<TVariables, TData>.Idle);

    private void SetState(Func<MutationState<TVariables, TData>, MutationState<TVariables, TData>> mutate)
    {
        MutationState<TVariables, TData> newState;
        lock (_gate)
        {
            newState = mutate(_state);
            _state = newState;
        }

        StateChanged?.Invoke(newState);
    }
}
