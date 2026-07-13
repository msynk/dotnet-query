namespace DotNetQuery;

/// <summary>
/// Bridges a real-time stream into the query cache. Each value emitted by an
/// <see cref="IAsyncEnumerable{T}"/> is written under a key with <c>SetQueryData</c>, so observers (and
/// the Blazor <c>&lt;Query&gt;</c> component, MVVM bindings, etc.) update live as data arrives.
/// </summary>
/// <remarks>
/// The source is transport-agnostic: it pairs directly with SignalR's
/// <c>HubConnection.StreamAsync&lt;T&gt;(...)</c>, a <see cref="System.Threading.Channels.Channel{T}"/>
/// reader, gRPC server streaming, or any async sequence — the core takes on no transport dependency.
/// </remarks>
public static class QueryStreamExtensions
{
    /// <summary>
    /// Writes each value emitted by <paramref name="source"/> into the cache under <paramref name="key"/>,
    /// replacing the previous value. Completes when the stream ends or <paramref name="cancellationToken"/>
    /// is cancelled.
    /// </summary>
    /// <typeparam name="T">The type of value produced by the stream (and cached).</typeparam>
    public static async Task StreamIntoCacheAsync<T>(
        this IQueryClient client,
        QueryKey key,
        IAsyncEnumerable<T> source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(source);

        await foreach (var value in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            client.SetQueryData(key, value);
        }
    }

    /// <summary>
    /// Folds each item emitted by <paramref name="source"/> into the cached value under
    /// <paramref name="key"/> via <paramref name="accumulate"/> (which receives the current cached value,
    /// or <see langword="default"/> if none, and the new item). Ideal for appending to a live list — a
    /// chat feed, an activity log, incremental results. Completes when the stream ends or is cancelled.
    /// </summary>
    /// <typeparam name="T">The accumulated (cached) type.</typeparam>
    /// <typeparam name="TItem">The type of each streamed item.</typeparam>
    public static async Task StreamIntoCacheAsync<T, TItem>(
        this IQueryClient client,
        QueryKey key,
        IAsyncEnumerable<TItem> source,
        Func<T?, TItem, T> accumulate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(accumulate);

        await foreach (var item in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var current = client.GetQueryData<T>(key).Data;
            client.SetQueryData(key, accumulate(current, item));
        }
    }
}
