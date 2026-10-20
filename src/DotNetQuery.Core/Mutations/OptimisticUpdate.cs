namespace DotNetQuery;

/// <summary>
/// A declarative optimistic update applied by a mutation before it runs and rolled back automatically if
/// it fails. Create instances with <see cref="OptimisticUpdate.Create{TVariables, TData}"/> and list them in
/// <see cref="MutationOptions{TVariables, TData}.OptimisticUpdates"/>.
/// </summary>
/// <typeparam name="TVariables">The mutation's variables type.</typeparam>
public abstract class OptimisticUpdate<TVariables>
{
    private protected OptimisticUpdate(QueryKey key) => Key = key;

    /// <summary>Gets the key of the query the update writes to.</summary>
    public QueryKey Key { get; }

    /// <summary>
    /// Cancels in-flight fetches for <see cref="Key"/>, applies the update, and returns the rollback action.
    /// </summary>
    internal abstract Task<Func<Task>> ApplyAsync(IQueryClient client, TVariables variables, CancellationToken cancellationToken);
}

/// <summary>Factory for <see cref="OptimisticUpdate{TVariables}"/>.</summary>
public static class OptimisticUpdate
{
    /// <summary>
    /// Creates an optimistic update that writes <paramref name="update"/>(current data, variables) to the
    /// query under <paramref name="key"/> (exact match). On failure the previous state is restored exactly
    /// (data, timestamp and staleness); if another write landed on top of the optimistic value in the
    /// meantime, the query is invalidated (refetched) instead so that write is not lost.
    /// </summary>
    /// <typeparam name="TVariables">The mutation's variables type.</typeparam>
    /// <typeparam name="TData">The cached data type of the target query.</typeparam>
    /// <param name="key">The key of the query to update.</param>
    /// <param name="update">
    /// Produces the optimistic value from the current cached value (or <see langword="default"/> when there is
    /// none) and the mutation's variables. Return a new instance rather than mutating the current one.
    /// </param>
    /// <example>
    /// <code>
    /// OptimisticUpdates =
    /// [
    ///     OptimisticUpdate.Create&lt;Todo, List&lt;Todo&gt;&gt;(TodoKeys.All, (todos, added) => [.. todos ?? [], added]),
    /// ],
    /// Invalidates = [TodoKeys.All],
    /// </code>
    /// </example>
    public static OptimisticUpdate<TVariables> Create<TVariables, TData>(QueryKey key, Func<TData?, TVariables, TData> update)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(update);
        return new TypedOptimisticUpdate<TVariables, TData>(key, update);
    }

    private sealed class TypedOptimisticUpdate<TVariables, TData>(QueryKey key, Func<TData?, TVariables, TData> update)
        : OptimisticUpdate<TVariables>(key)
    {
        internal override async Task<Func<Task>> ApplyAsync(IQueryClient client, TVariables variables, CancellationToken cancellationToken)
        {
            // Stop a pending refetch from overwriting the optimistic value when it lands.
            await client.CancelQueriesAsync(k => k == Key, cancellationToken).ConfigureAwait(false);

            while (true)
            {
                var entry = client.Cache.GetOrCreate<TData>(Key);
                if (entry.TryUpdateDataCore(current => update(current, variables)) is not { } write)
                {
                    continue; // collected concurrently; retry against a fresh entry
                }

                return () =>
                {
                    // Restore the exact snapshot (data, timestamp, invalidation) - unless something else wrote
                    // on top of the optimistic value meanwhile (another mutation's update, a refetch). Restoring
                    // then would clobber that write, so mark the query stale and refetch it instead.
                    return entry.TryRestore(write.Previous, write.PreviousInvalidated, write.Version)
                        ? Task.CompletedTask
                        : client.InvalidateQueriesAsync(k => k == Key, CancellationToken.None);
                };
            }
        }
    }
}
