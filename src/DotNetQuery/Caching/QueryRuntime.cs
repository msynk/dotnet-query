namespace DotNetQuery;

/// <summary>
/// Shared, per-cache services handed to every entry: the clock, the diagnostics sink, connectivity, and
/// the cache's change notifications. Keeps <see cref="QueryEntry{T}"/> decoupled from the client.
/// </summary>
internal sealed class QueryRuntime
{
    private readonly QueryCache _cache;

    public QueryRuntime(
        QueryCache cache,
        TimeProvider timeProvider,
        IQueryEventSink? eventSink,
        IConnectivityProvider? connectivity,
        TimeSpan defaultCacheTime,
        QueryClientOptions? options = null)
    {
        _cache = cache;
        TimeProvider = timeProvider;
        EventSink = eventSink;
        Connectivity = connectivity;
        DefaultCacheTime = defaultCacheTime;
        Options = options;
        Limiter = options?.MaxConcurrentFetches is { } max && max > 0 ? new FetchLimiter(max) : null;
        Interceptors = options is { Interceptors.Count: > 0 } ? [.. options.Interceptors] : [];
    }

    /// <summary>The owning client's options, when the cache belongs to a client.</summary>
    public QueryClientOptions? Options { get; }

    private long _sequence;

    /// <summary>
    /// The next value of a logical clock shared by the cache's fetches and mutations: a fetch cycle that started with a
    /// higher value than the one a mutation took after its server call settled was requested after that call, so its
    /// result already reflects the write (timestamps cannot order events reliably under a fake or coarse clock).
    /// </summary>
    public long NextSequence() => Interlocked.Increment(ref _sequence);

    /// <summary>The client-wide concurrency limit for fetch attempts, if any.</summary>
    public FetchLimiter? Limiter { get; }

    /// <summary>The client-wide rate limiter for fetch attempts, if any.</summary>
    public IFetchRateLimiter? RateLimiter => Options?.FetchRateLimiter;

    /// <summary>Interceptors around every fetch attempt, outermost first.</summary>
    public IQueryInterceptor[] Interceptors { get; }

    public TimeProvider TimeProvider { get; }

    public IQueryEventSink? EventSink { get; }

    public IConnectivityProvider? Connectivity { get; }

    public TimeSpan DefaultCacheTime { get; }

    /// <summary>Whether fetches may hit the network. Always true without a connectivity provider.</summary>
    public bool IsOnline => Connectivity?.IsOnline ?? true;

    /// <summary>Completes when the connectivity provider reports online (immediately if it already does).</summary>
    public Task WhenOnlineAsync(CancellationToken cancellationToken)
    {
        if (Connectivity is not { } connectivity || connectivity.IsOnline)
        {
            return Task.CompletedTask;
        }

        return WaitAsync(connectivity, cancellationToken);

        static async Task WaitAsync(IConnectivityProvider connectivity, CancellationToken cancellationToken)
        {
            var online = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(bool isOnline)
            {
                if (isOnline)
                {
                    online.TrySetResult();
                }
            }

            connectivity.ConnectivityChanged += Handler;
            try
            {
                // Re-check after subscribing so a reconnect between the first check and the subscription is not missed.
                if (!connectivity.IsOnline)
                {
                    await online.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                connectivity.ConnectivityChanged -= Handler;
            }
        }
    }

    public void RaiseChanged(QueryKey key, QueryCacheChangeKind kind) => _cache.RaiseChanged(key, kind);
}

/// <summary>How a fetch request treats staleness and an already in-flight fetch.</summary>
internal enum FetchMode
{
    /// <summary>Serve fresh cached data; otherwise fetch, joining any in-flight fetch.</summary>
    IfStale,

    /// <summary>Fetch even when fresh, but join (deduplicate with) any in-flight fetch.</summary>
    Force,

    /// <summary>
    /// Fetch even when fresh, cancelling any in-flight fetch first so the result reflects the latest
    /// server state (used by invalidation and explicit refetches). Waiters of the cancelled fetch are
    /// handed over to the new one.
    /// </summary>
    CancelAndRefetch,
}
