namespace DotNetQuery;

/// <summary>
/// Bulk <see cref="IQueryClient"/> operations over every cached query that matches a <see cref="QueryFilter"/>:
/// invalidate, refetch, cancel, reset, remove and count.
/// </summary>
public static class QueryFilterExtensions
{
    /// <summary>
    /// Marks every query matching <paramref name="filter"/> as stale, then refetches the ones selected by
    /// <paramref name="refetchType"/> (by default those currently observed), cancelling any fetch in flight for
    /// them first. Disabled queries are marked stale but never refetched.
    /// </summary>
    public static async Task InvalidateQueriesAsync(
        this IQueryClient client,
        QueryFilter filter,
        RefetchType refetchType = RefetchType.Active,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(filter);

        var matched = client.Cache.Invalidate(entry => entry.Matches(filter), filter.Key is { } key ? [key] : null);
        var refetches = new List<Task>();
        foreach (var (entry, refetchActive) in matched)
        {
            var refetch = refetchType switch
            {
                RefetchType.Active => refetchActive,
                RefetchType.Inactive => !entry.IsActive,
                RefetchType.All => true,
                _ => false,
            };

            if (refetch)
            {
                refetches.Add(entry.RefetchAsync(FetchMode.CancelAndRefetch, includeDisabled: false, cancellationToken));
            }
        }

        if (refetches.Count > 0)
        {
            await Task.WhenAll(refetches).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Refetches every query matching <paramref name="filter"/> (every query when <see langword="null"/>) with its
    /// last-used fetcher, regardless of staleness, cancelling any fetch in flight first. Disabled queries and
    /// queries that were never fetched are skipped. Never throws for fetch failures (they are captured in each
    /// query's state). Completes once every refetch has settled.
    /// </summary>
    public static Task RefetchQueriesAsync(this IQueryClient client, QueryFilter? filter = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var refetches = new List<Task>();
        foreach (var entry in client.Cache.FindEntries(filter))
        {
            var task = entry.RefetchAsync(FetchMode.CancelAndRefetch, includeDisabled: false, cancellationToken);
            if (!task.IsCompleted)
            {
                refetches.Add(task);
            }
        }

        return refetches.Count == 0 ? Task.CompletedTask : Task.WhenAll(refetches);
    }

    /// <summary>
    /// Cancels the in-flight fetch of every query matching <paramref name="filter"/>, restoring each query's
    /// previous state. Completes once the cancelled fetches have settled.
    /// </summary>
    public static Task CancelQueriesAsync(this IQueryClient client, QueryFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(filter);
        var cancellations = new List<Task>();
        foreach (var entry in client.Cache.FindEntries(filter))
        {
            var task = entry.CancelAsync();
            if (!task.IsCompleted)
            {
                cancellations.Add(task);
            }
        }

        return cancellations.Count == 0 ? Task.CompletedTask : Task.WhenAll(cancellations).WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Resets every query matching <paramref name="filter"/> (every query when <see langword="null"/>) to its initial
    /// state - cancelling any fetch in flight and discarding its data and error - and then refetches the reset
    /// queries that are currently observed. Use it to start over after the inputs behind many queries changed (for
    /// example a tenant switch) without unmounting the screens that show them.
    /// </summary>
    public static async Task ResetQueriesAsync(this IQueryClient client, QueryFilter? filter = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var matched = client.Cache.FindEntries(filter);
        if (matched.Count == 0)
        {
            return;
        }

        // Reset everything first, start the refetches of observed queries, and only then cancel the detached
        // fetches, so their waiters are handed to the refetch instead of resolving with an empty state.
        var cancellations = matched.Select(entry => entry.ResetAndDetach()).ToList();

        var refetches = new List<Task>();
        foreach (var entry in matched)
        {
            if (entry.IsActive)
            {
                refetches.Add(entry.RefetchAsync(FetchMode.CancelAndRefetch, includeDisabled: false, cancellationToken));
            }
        }

        cancellations.ForEach(cancel => cancel());

        if (refetches.Count > 0)
        {
            await Task.WhenAll(refetches).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Removes every query matching <paramref name="filter"/> (every query when <see langword="null"/>) from the
    /// cache, cancelling in-flight fetches. Observers of a removed query keep their last state; a later fetch or
    /// <c>Watch</c> starts a fresh entry.
    /// </summary>
    /// <returns>The number of queries removed.</returns>
    public static int RemoveQueries(this IQueryClient client, QueryFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        var removed = 0;
        foreach (var entry in client.Cache.FindEntries(filter))
        {
            if (client.Cache.Remove(entry))
            {
                removed++;
            }
        }

        return removed;
    }

    /// <summary>
    /// Returns how many queries matching <paramref name="filter"/> (all queries when <see langword="null"/>) are
    /// fetching right now - for a global "syncing" indicator.
    /// </summary>
    public static int CountFetching(this IQueryClient client, QueryFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        var count = 0;
        foreach (var entry in client.Cache.FindEntries(filter))
        {
            if (entry.IsFetching)
            {
                count++;
            }
        }

        return count;
    }
}
