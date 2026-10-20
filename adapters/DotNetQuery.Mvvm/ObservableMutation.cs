using System.ComponentModel;

namespace DotNetQuery.Mvvm;

/// <summary>
/// An <see cref="INotifyPropertyChanged"/> view of a mutation, for data binding in XAML UI frameworks.
/// Wraps a <see cref="Mutation{TVariables, TData}"/> and marshals state changes onto the UI thread captured
/// at construction.
/// </summary>
/// <typeparam name="TVariables">The type of the variables passed to the mutation.</typeparam>
/// <typeparam name="TData">The type of data returned by the mutation.</typeparam>
public sealed class ObservableMutation<TVariables, TData> : INotifyPropertyChanged, IDisposable
{
    private static readonly string[] PropertyNames =
    [
        nameof(Data), nameof(Error), nameof(Status), nameof(Variables), nameof(IsIdle), nameof(IsPending),
        nameof(IsSuccess), nameof(IsError), nameof(FailureCount),
    ];

    private readonly Mutation<TVariables, TData> _mutation;
    private readonly SynchronizationContext? _syncContext;
    private readonly object _applyGate = new();
    private MutationState<TVariables, TData> _state;
    private long _sequence;
    private long _applied;

    /// <summary>Creates an observable mutation bound to a mutation function.</summary>
    public ObservableMutation(
        IQueryClient client,
        Func<TVariables, CancellationToken, Task<TData>> mutationFn,
        MutationOptions<TVariables, TData>? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(mutationFn);

        _syncContext = SynchronizationContext.Current;
        _mutation = client.CreateMutation(mutationFn, options);
        _state = _mutation.CurrentState;
        _mutation.StateChanged += OnStateChanged;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Gets the data returned by the most recent successful run, if any.</summary>
    public TData? Data => _state.Data;

    /// <summary>Gets the error from the most recent failed run, if any.</summary>
    public Exception? Error => _state.Error;

    /// <summary>Gets the lifecycle status of the mutation.</summary>
    public MutationStatus Status => _state.Status;

    /// <summary>Gets the variables of the most recent run, if any.</summary>
    public TVariables? Variables => _state.Variables;

    /// <summary>Gets a value indicating whether the mutation has not run (or was reset).</summary>
    public bool IsIdle => _state.IsIdle;

    /// <summary>Gets a value indicating whether the mutation is currently running.</summary>
    public bool IsPending => _state.IsPending;

    /// <summary>Gets a value indicating whether the mutation completed successfully.</summary>
    public bool IsSuccess => _state.IsSuccess;

    /// <summary>Gets a value indicating whether the mutation failed.</summary>
    public bool IsError => _state.IsError;

    /// <summary>Gets the number of failed attempts in the most recent run.</summary>
    public int FailureCount => _state.FailureCount;

    /// <summary>Runs the mutation with the supplied variables.</summary>
    public Task<MutationResult<TVariables, TData>> MutateAsync(TVariables variables, CancellationToken cancellationToken = default) =>
        _mutation.MutateAsync(variables, cancellationToken);

    /// <summary>Resets the mutation to its idle state.</summary>
    public void Reset() => _mutation.Reset();

    private void OnStateChanged(MutationState<TVariables, TData> state)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        if (_syncContext is null || _syncContext == SynchronizationContext.Current)
        {
            Apply(state, sequence);
        }
        else
        {
            _syncContext.Post(
                static s =>
                {
                    var (self, state, sequence) = ((ObservableMutation<TVariables, TData>, MutationState<TVariables, TData>, long))s!;
                    self.Apply(state, sequence);
                },
                (this, state, sequence));
        }
    }

    // Applied on the UI thread together with the notifications, so bound values match the notification;
    // a notification overtaken by a newer one is dropped.
    private void Apply(MutationState<TVariables, TData> state, long sequence)
    {
        lock (_applyGate)
        {
            if (sequence <= _applied)
            {
                return;
            }

            _applied = sequence;
            _state = state;
        }

        foreach (var name in PropertyNames)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>Stops forwarding state changes.</summary>
    public void Dispose() => _mutation.StateChanged -= OnStateChanged;
}
