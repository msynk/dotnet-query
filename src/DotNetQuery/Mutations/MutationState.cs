namespace DotNetQuery;

/// <summary>
/// An immutable snapshot of a mutation's state.
/// </summary>
/// <typeparam name="TVariables">The type of the variables passed to the mutation.</typeparam>
/// <typeparam name="TData">The type of data returned by the mutation.</typeparam>
public sealed record MutationState<TVariables, TData>
{
    /// <summary>Gets the lifecycle status of the mutation.</summary>
    public required MutationStatus Status { get; init; }

    /// <summary>Gets the data returned by the most recent successful run, if any.</summary>
    public TData? Data { get; init; }

    /// <summary>Gets the error from the most recent failed run, if any.</summary>
    public Exception? Error { get; init; }

    /// <summary>Gets the variables of the most recent run, if any.</summary>
    public TVariables? Variables { get; init; }

    /// <summary>Gets the UTC timestamp at which the most recent run was submitted.</summary>
    public DateTimeOffset? SubmittedAt { get; init; }

    /// <summary>Gets the number of failed attempts within the current run.</summary>
    public int FailureCount { get; init; }

    /// <summary>Gets a value indicating whether the mutation has never run.</summary>
    public bool IsIdle => Status is MutationStatus.Idle;

    /// <summary>Gets a value indicating whether the mutation is currently running.</summary>
    public bool IsPending => Status is MutationStatus.Pending;

    /// <summary>Gets a value indicating whether the mutation completed successfully.</summary>
    public bool IsSuccess => Status is MutationStatus.Success;

    /// <summary>Gets a value indicating whether the mutation failed.</summary>
    public bool IsError => Status is MutationStatus.Error;

    /// <summary>The initial idle state for a mutation that has not yet run.</summary>
    public static MutationState<TVariables, TData> Idle { get; } = new() { Status = MutationStatus.Idle };
}
