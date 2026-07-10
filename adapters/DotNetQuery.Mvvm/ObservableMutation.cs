using System.ComponentModel;

namespace DotNetQuery.Mvvm;

/// <summary>
/// An <see cref="INotifyPropertyChanged"/> view of a mutation, for data binding in XAML UI frameworks.
/// Wraps a <see cref="Mutation{TVariables, TData}"/> and marshals change notifications onto the UI
/// thread captured at construction.
/// </summary>
/// <typeparam name="TVariables">The type of the variables passed to the mutation.</typeparam>
/// <typeparam name="TData">The type of data returned by the mutation.</typeparam>
public sealed class ObservableMutation<TVariables, TData> : INotifyPropertyChanged
{
    private readonly Mutation<TVariables, TData> _mutation;
    private readonly SynchronizationContext? _syncContext;
    private MutationState<TVariables, TData> _state;

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

    /// <summary>Gets a value indicating whether the mutation is currently running.</summary>
    public bool IsPending => _state.IsPending;

    /// <summary>Gets a value indicating whether the mutation completed successfully.</summary>
    public bool IsSuccess => _state.IsSuccess;

    /// <summary>Gets a value indicating whether the mutation failed.</summary>
    public bool IsError => _state.IsError;

    /// <summary>Runs the mutation with the supplied variables.</summary>
    public Task<MutationResult<TVariables, TData>> MutateAsync(TVariables variables, CancellationToken cancellationToken = default) =>
        _mutation.MutateAsync(variables, cancellationToken);

    /// <summary>Resets the mutation to its idle state.</summary>
    public void Reset() => _mutation.Reset();

    private void OnStateChanged(MutationState<TVariables, TData> state)
    {
        _state = state;
        if (_syncContext is null || _syncContext == SynchronizationContext.Current)
        {
            RaiseAll();
        }
        else
        {
            _syncContext.Post(static self => ((ObservableMutation<TVariables, TData>)self!).RaiseAll(), this);
        }
    }

    private void RaiseAll()
    {
        foreach (var name in PropertyNames)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    private static readonly string[] PropertyNames =
        [nameof(Data), nameof(Error), nameof(Status), nameof(IsPending), nameof(IsSuccess), nameof(IsError)];
}
