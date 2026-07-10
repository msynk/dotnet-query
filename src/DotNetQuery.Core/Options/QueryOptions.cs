namespace DotNetQuery;

/// <summary>
/// Per-query configuration controlling caching, staleness, retries and background behavior.
/// Any property left <see langword="null"/> falls back to the client-wide default in
/// <see cref="QueryClientOptions"/>.
/// </summary>
public sealed record QueryOptions
{
    /// <summary>
    /// How long fetched data is considered fresh. While fresh, requests are served from cache
    /// without hitting the origin. Defaults to <see cref="QueryClientOptions.DefaultStaleTime"/>.
    /// </summary>
    public TimeSpan? StaleTime { get; init; }

    /// <summary>
    /// How long an inactive query (no observers) is retained before garbage collection.
    /// Defaults to <see cref="QueryClientOptions.DefaultCacheTime"/>.
    /// </summary>
    public TimeSpan? CacheTime { get; init; }

    /// <summary>
    /// Maximum number of automatic retries after a failed fetch.
    /// Defaults to <see cref="QueryClientOptions.DefaultRetryCount"/>.
    /// </summary>
    public int? RetryCount { get; init; }

    /// <summary>
    /// Strategy computing the delay between retries.
    /// Defaults to <see cref="QueryClientOptions.DefaultRetryDelay"/>.
    /// </summary>
    public RetryDelayStrategy? RetryDelay { get; init; }

    /// <summary>
    /// Interval at which the query automatically refetches in the background.
    /// <see langword="null"/> disables periodic refetching.
    /// </summary>
    public TimeSpan? RefetchInterval { get; init; }

    /// <summary>
    /// Whether the query is allowed to run. When <see langword="false"/> the query stays idle.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    public bool? Enabled { get; init; }

    /// <summary>Whether to refetch stale queries when network connectivity is restored.</summary>
    public bool? RefetchOnReconnect { get; init; }

    /// <summary>Whether to refetch stale queries when the application/window regains focus.</summary>
    public bool? RefetchOnFocus { get; init; }

    /// <summary>Whether to refetch stale queries when the application resumes from background.</summary>
    public bool? RefetchOnResume { get; init; }

    /// <summary>
    /// Whether a fetch failure should surface as a thrown exception from awaiting APIs,
    /// rather than being captured into <see cref="QueryState{T}.Error"/>.
    /// </summary>
    public bool? ThrowOnError { get; init; }
}
