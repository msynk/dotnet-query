namespace DotNetQuery;

/// <summary>The public-facing result of a mutation run.</summary>
/// <typeparam name="TVariables">The type of the variables passed to the mutation.</typeparam>
/// <typeparam name="TData">The type of data returned by the mutation.</typeparam>
public readonly record struct MutationResult<TVariables, TData>
{
    private readonly MutationState<TVariables, TData> _state;

    internal MutationResult(MutationState<TVariables, TData> state) => _state = state;

    /// <summary>Gets the full immutable state snapshot backing this result.</summary>
    public MutationState<TVariables, TData> State => _state;

    /// <summary>Gets the data returned by the mutation, if any.</summary>
    public TData? Data => _state.Data;

    /// <summary>Gets the error from the mutation, if any.</summary>
    public Exception? Error => _state.Error;

    /// <summary>Gets the lifecycle status of the mutation.</summary>
    public MutationStatus Status => _state.Status;

    /// <summary>Gets a value indicating whether the mutation completed successfully.</summary>
    public bool IsSuccess => _state.IsSuccess;

    /// <summary>Gets a value indicating whether the mutation failed.</summary>
    public bool IsError => _state.IsError;

    /// <summary>Returns the data if the mutation succeeded; otherwise throws the captured error.</summary>
    public TData EnsureData()
    {
        if (_state.IsSuccess)
        {
            return _state.Data!;
        }

        throw _state.Error ?? new InvalidOperationException($"Mutation did not succeed (status: {_state.Status}).");
    }
}
