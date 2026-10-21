namespace DotNetQuery;

/// <summary>
/// Configuration and lifecycle callbacks for a mutation.
/// </summary>
/// <typeparam name="TVariables">The type of the variables passed to the mutation.</typeparam>
/// <typeparam name="TData">The type of data returned by the mutation.</typeparam>
/// <remarks>
/// The callbacks fire in this order:
/// <see cref="OnMutate"/> (before the mutation, for optimistic updates) →
/// <see cref="OnSuccess"/> or <see cref="OnError"/> → <see cref="OnSettled"/> (always).
/// The value returned from <see cref="OnMutate"/> is threaded through the remaining callbacks as the
/// <c>context</c> argument, enabling rollback of an optimistic update inside <see cref="OnError"/>.
/// For the common cases, prefer the declarative <see cref="OptimisticUpdates"/> (applied, and rolled back
/// on failure, automatically) and <see cref="Invalidates"/> (refetched once the mutation settles).
/// </remarks>
public sealed record MutationOptions<TVariables, TData>
{
    /// <summary>
    /// An optional key identifying the kind of mutation (for example <c>["todos", "add"]</c>). Reported in
    /// diagnostics events and shown by the DevTools mutation history.
    /// </summary>
    public QueryKey? MutationKey { get; init; }

    /// <summary>
    /// Declarative optimistic updates, created with <see cref="OptimisticUpdate.Create{TVariables, TData}"/>.
    /// Before the mutation runs, each one cancels in-flight fetches for its key, snapshots the cached data,
    /// and writes the optimistic value; if the mutation fails (or is cancelled) every snapshot is restored
    /// in reverse order. Combine with <see cref="Invalidates"/> so the server's truth replaces the optimistic
    /// value once the mutation settles.
    /// </summary>
    public IReadOnlyList<OptimisticUpdate<TVariables>>? OptimisticUpdates { get; init; }

    /// <summary>
    /// Query keys (prefix-matched) to invalidate once the mutation settles - after success or failure, so
    /// a failed optimistic update is also reconciled with the server. The mutation remains pending until the
    /// resulting refetches of active queries complete.
    /// </summary>
    public IReadOnlyList<QueryKey>? Invalidates { get; init; }

    /// <summary>Maximum number of automatic retries after a failed mutation. Defaults to 0.</summary>
    public int RetryCount { get; init; }

    /// <summary>Strategy computing the delay between retries. Defaults to no delay.</summary>
    public RetryDelayStrategy? RetryDelay { get; init; }

    /// <summary>
    /// Whether a failure should surface as a thrown exception from <c>MutateAsync</c> rather than
    /// being captured into <see cref="MutationState{TVariables, TData}.Error"/>. Defaults to <see langword="true"/>.
    /// </summary>
    public bool ThrowOnError { get; init; } = true;

    /// <summary>
    /// Runs before the mutation function. Use it to apply an optimistic update and return a context
    /// object (for example the previous value) that later callbacks can use to roll back.
    /// </summary>
    public Func<TVariables, CancellationToken, ValueTask<object?>>? OnMutate { get; init; }

    /// <summary>Runs after the mutation succeeds, with the returned data and the optimistic context.</summary>
    public Func<TData, TVariables, object?, CancellationToken, ValueTask>? OnSuccess { get; init; }

    /// <summary>Runs after the mutation fails, with the error and the optimistic context (roll back here).</summary>
    public Func<Exception, TVariables, object?, CancellationToken, ValueTask>? OnError { get; init; }

    /// <summary>Runs after the mutation settles, whether it succeeded or failed.</summary>
    public Func<TData?, Exception?, TVariables, object?, CancellationToken, ValueTask>? OnSettled { get; init; }
}
