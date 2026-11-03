namespace DotNetQuery;

/// <summary>Options for a <see cref="BatchLoader{TKey, TValue}"/>.</summary>
public sealed record BatchLoaderOptions
{
    /// <summary>
    /// How long to collect keys before sending a batch. <see cref="TimeSpan.Zero"/> (the default) collects the keys
    /// requested in the same burst (until the thread pool picks up the dispatch).
    /// </summary>
    public TimeSpan Delay { get; init; }

    /// <summary>The most keys per batch request; larger bursts are split. Defaults to 100.</summary>
    public int MaxBatchSize { get; init; } = 100;

    /// <summary>The clock used for <see cref="Delay"/>. Defaults to <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider? TimeProvider { get; init; }
}

/// <summary>
/// Coalesces many single-item loads into batch requests (the DataLoader pattern): queries created for many ids at
/// once - a list that renders one detail component per row - each call <see cref="LoadAsync"/>, and the loader sends
/// one request for all of them. Each query still caches, invalidates and refetches on its own; deduplication of the
/// same key is the query cache's job.
/// </summary>
/// <example>
/// <code>
/// var users = new BatchLoader&lt;int, User&gt;((ids, ct) =&gt; api.GetUsersAsync(ids, ct)); // returns a dictionary by id
///
/// public static QueryDefinition&lt;User&gt; ById(int id) =&gt; new(QueryKey.Create("user", id), ct =&gt; users.LoadAsync(id, ct));
/// </code>
/// </example>
/// <typeparam name="TKey">The item key type.</typeparam>
/// <typeparam name="TValue">The item type.</typeparam>
public sealed class BatchLoader<TKey, TValue>
    where TKey : notnull
{
    private readonly Func<IReadOnlyList<TKey>, CancellationToken, Task<IReadOnlyDictionary<TKey, TValue>>> _fetchBatch;
    private readonly BatchLoaderOptions _options;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private Dictionary<TKey, TaskCompletionSource<TValue>>? _pending;

    /// <summary>Creates a loader over a batch fetch that returns the found items by key (missing keys fail with <see cref="KeyNotFoundException"/>).</summary>
    public BatchLoader(
        Func<IReadOnlyList<TKey>, CancellationToken, Task<IReadOnlyDictionary<TKey, TValue>>> fetchBatch,
        BatchLoaderOptions? options = null)
    {
        _fetchBatch = fetchBatch ?? throw new ArgumentNullException(nameof(fetchBatch));
        _options = options ?? new BatchLoaderOptions();
        _time = _options.TimeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Loads one item as part of the next batch. Cancelling stops only this caller's wait: the batch request is shared,
    /// so it is not cancelled for the other callers.
    /// </summary>
    public Task<TValue> LoadAsync(TKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        TaskCompletionSource<TValue> waiter;
        var schedule = false;
        lock (_gate)
        {
            if (_pending is null)
            {
                _pending = [];
                schedule = true;
            }

            if (!_pending.TryGetValue(key, out waiter!))
            {
                waiter = new TaskCompletionSource<TValue>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending[key] = waiter;
            }
        }

        if (schedule)
        {
            _ = DispatchLaterAsync();
        }

        return waiter.Task.WaitAsync(cancellationToken);
    }

    private async Task DispatchLaterAsync()
    {
        if (_options.Delay > TimeSpan.Zero)
        {
            await Task.Delay(_options.Delay, _time).ConfigureAwait(false);
        }
        else
        {
            await Task.Yield();
        }

        Dictionary<TKey, TaskCompletionSource<TValue>> batch;
        lock (_gate)
        {
            batch = _pending!;
            _pending = null;
        }

        foreach (var chunk in batch.Chunk(Math.Max(1, _options.MaxBatchSize)))
        {
            await RunAsync(chunk).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(KeyValuePair<TKey, TaskCompletionSource<TValue>>[] chunk)
    {
        IReadOnlyDictionary<TKey, TValue> found;
        try
        {
            found = await _fetchBatch(chunk.Select(pair => pair.Key).ToArray(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            foreach (var (_, waiter) in chunk)
            {
                waiter.TrySetException(ex);
            }

            return;
        }

        foreach (var (key, waiter) in chunk)
        {
            if (found.TryGetValue(key, out var value))
            {
                waiter.TrySetResult(value);
            }
            else
            {
                waiter.TrySetException(new KeyNotFoundException($"The batch response did not include '{key}'."));
            }
        }
    }
}
