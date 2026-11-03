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
    /// Runs of mutations that share a scope id execute one at a time, in the order they were started - even across
    /// different <see cref="Mutation{TVariables, TData}"/> instances - so dependent writes (create, then rename)
    /// reach the server in order. A run waiting for its turn is pending with
    /// <see cref="MutationState{TVariables, TData}.IsPaused"/> set; its optimistic updates and
    /// <see cref="OnMutate"/> still apply immediately, only the mutation function waits. <see langword="null"/>
    /// (the default) runs immediately and concurrently.
    /// </summary>
    public string? Scope { get; init; }

    /// <summary>
    /// How the mutation behaves while offline. With <see cref="DotNetQuery.NetworkMode.Online"/> a run started
    /// offline waits (pending, <see cref="MutationState{TVariables, TData}.IsPaused"/>) until connectivity returns,
    /// and a run that loses connectivity between retries waits before retrying; optimistic updates still apply
    /// immediately. <see cref="DotNetQuery.NetworkMode.OfflineFirst"/> makes the first attempt regardless and waits
    /// only before retries. Defaults to <see cref="QueryClientOptions.DefaultMutationNetworkMode"/>
    /// (<see cref="DotNetQuery.NetworkMode.Always"/>).
    /// </summary>
    public NetworkMode? NetworkMode { get; init; }

    /// <summary>
    /// When <see langword="true"/>, calling <c>MutateAsync</c> on the same <see cref="Mutation{TVariables, TData}"/>
    /// with variables equal to those of a run still pending joins that run instead of starting another (a
    /// double-clicked "mark as read"). Only for idempotent operations. Defaults to <see langword="false"/>.
    /// </summary>
    public bool DeduplicatePending { get; init; }

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
    /// Decides whether a failed attempt is worth retrying (for example only transient network errors - see
    /// <c>HttpRetryPolicy.ShouldRetry</c>). Consulted only while <see cref="RetryCount"/> is not exhausted; a
    /// predicate that throws counts as "do not retry". <see langword="null"/> retries every failure.
    /// </summary>
    public RetryPredicate? ShouldRetry { get; init; }

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

/// <summary>
/// Defaults applied to mutations - all of them, or those whose <see cref="MutationOptions{TVariables, TData}.MutationKey"/>
/// starts with a prefix (see <see cref="QueryClientOptions.SetMutationDefaults(QueryKey, MutationDefaults)"/>). A mutation's
/// own options win; unset properties fall back to the most specific matching defaults.
/// </summary>
public sealed record MutationDefaults
{
    /// <summary>Retries, used when the mutation's own <c>RetryCount</c> is 0.</summary>
    public int? RetryCount { get; init; }

    /// <summary>The retry delay, used when the mutation sets none.</summary>
    public RetryDelayStrategy? RetryDelay { get; init; }

    /// <summary>The retry predicate, used when the mutation sets none.</summary>
    public RetryPredicate? ShouldRetry { get; init; }

    /// <summary>The network mode, used when the mutation sets none.</summary>
    public NetworkMode? NetworkMode { get; init; }

    /// <summary>The serialization scope, used when the mutation sets none.</summary>
    public string? Scope { get; init; }

    /// <summary>Query keys to invalidate once the mutation settles, used when the mutation lists none.</summary>
    public IReadOnlyList<QueryKey>? Invalidates { get; init; }

    internal static MutationDefaults? Merge(MutationDefaults? specific, MutationDefaults? fallback) =>
        specific is null ? fallback : fallback is null ? specific : new MutationDefaults
        {
            RetryCount = specific.RetryCount ?? fallback.RetryCount,
            RetryDelay = specific.RetryDelay ?? fallback.RetryDelay,
            ShouldRetry = specific.ShouldRetry ?? fallback.ShouldRetry,
            NetworkMode = specific.NetworkMode ?? fallback.NetworkMode,
            Scope = specific.Scope ?? fallback.Scope,
            Invalidates = specific.Invalidates ?? fallback.Invalidates,
        };

    internal MutationOptions<TVariables, TData> ApplyTo<TVariables, TData>(MutationOptions<TVariables, TData> options) => options with
    {
        RetryCount = options.RetryCount != 0 ? options.RetryCount : RetryCount ?? 0,
        RetryDelay = options.RetryDelay ?? RetryDelay,
        ShouldRetry = options.ShouldRetry ?? ShouldRetry,
        NetworkMode = options.NetworkMode ?? NetworkMode,
        Scope = options.Scope ?? Scope,
        Invalidates = options.Invalidates ?? Invalidates,
    };
}

/// <summary>Mutation factories beyond <see cref="IQueryClient.CreateMutation{TVariables, TData}"/>.</summary>
public static class MutationExtensions
{
    /// <summary>
    /// Creates a mutation whose function reports progress (for example upload percentage, 0 to 1): every report updates
    /// <see cref="MutationState{TVariables, TData}.Progress"/> and notifies observers, and appears in the mutation cache.
    /// </summary>
    /// <example>
    /// <code>
    /// var upload = client.CreateMutation&lt;Stream, Uri&gt;((file, progress, ct) =&gt; api.UploadAsync(file, progress, ct));
    /// upload.StateChanged += s =&gt; progressBar.Value = s.Progress ?? 0;
    /// </code>
    /// </example>
    public static Mutation<TVariables, TData> CreateMutation<TVariables, TData>(
        this IQueryClient client,
        Func<TVariables, IProgress<double>, CancellationToken, Task<TData>> mutationFn,
        MutationOptions<TVariables, TData>? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(mutationFn);
        return client is QueryClient queryClient
            ? queryClient.CreateMutationCore(null, mutationFn, options)
            : client.CreateMutation<TVariables, TData>((variables, ct) => mutationFn(variables, new Progress<double>(), ct), options);
    }
}
