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
    /// Decides whether a failure is worth retrying (for example retrying transient network errors but not a
    /// 401 or 404 - see <c>HttpRetryPolicy.ShouldRetry</c>). Consulted only while <see cref="RetryCount"/> is
    /// not exhausted; a predicate that throws counts as "do not retry". Defaults to
    /// <see cref="QueryClientOptions.DefaultShouldRetry"/> (retry every failure).
    /// </summary>
    public RetryPredicate? ShouldRetry { get; init; }

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

    /// <summary>
    /// Whether an in-flight fetch should be cancelled when its last observer unsubscribes (for example
    /// when a component using the query is disposed mid-fetch). When <see langword="false"/> the fetch
    /// runs to completion and populates the cache for later use. Defaults to
    /// <see cref="QueryClientOptions.DefaultCancelInFlightWhenUnobserved"/>.
    /// </summary>
    public bool? CancelInFlightWhenUnobserved { get; init; }

    /// <summary>
    /// Whether a refetch that returns data structurally equal to the cached data keeps the cached instance
    /// (and, inside writable lists and dictionaries, reuses every unchanged element), so references stay stable
    /// and UIs can skip re-rendering unchanged parts. Equality is <see cref="object.Equals(object?)"/> (records,
    /// strings, primitives) applied element by element through lists and dictionaries; types whose
    /// <c>Equals</c> does not compare their full value (for example identity-only equality) must not enable it.
    /// Defaults to <see cref="QueryClientOptions.DefaultStructuralSharing"/>.
    /// </summary>
    public bool? StructuralSharing { get; init; }

    /// <summary>
    /// How the query behaves while offline (see <see cref="DotNetQuery.NetworkMode"/>). Defaults to
    /// <see cref="QueryClientOptions.DefaultNetworkMode"/>.
    /// </summary>
    public NetworkMode? NetworkMode { get; init; }

    /// <summary>
    /// The longest a single fetch attempt may take. A slower attempt is cancelled and fails with a
    /// <see cref="TimeoutException"/>, which is retried like any other failure (and is transient for
    /// <see cref="HttpRetryPolicy.ShouldRetry"/>). <see langword="null"/> (the default) never times out.
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// The query's priority for the client's concurrency limit (<see cref="QueryClientOptions.MaxConcurrentFetches"/>):
    /// waiting attempts with a higher priority start first. Defaults to 0.
    /// </summary>
    public int? Priority { get; init; }

    /// <summary>
    /// Computes the refetch interval after every fetch from the query's current description (for example poll fast
    /// while a job is running and stop once it finished: <c>q =&gt; q.Data is Job { Done: false } ? TimeSpan.FromSeconds(1) : null</c>).
    /// Returning <see langword="null"/> (or a non-positive interval) stops polling until the next fetch. Takes
    /// precedence over <see cref="RefetchInterval"/>. Runs outside cache locks; a throwing function stops polling.
    /// </summary>
    public Func<QueryDescriptor, TimeSpan?>? AdaptiveRefetchInterval { get; init; }

    /// <summary>
    /// Layers <paramref name="specific"/> over <paramref name="fallback"/>: every property set on
    /// <paramref name="specific"/> wins, every other one comes from <paramref name="fallback"/>.
    /// </summary>
    internal static QueryOptions? Merge(QueryOptions? specific, QueryOptions? fallback)
    {
        if (fallback is null)
        {
            return specific;
        }

        if (specific is null)
        {
            return fallback;
        }

        return new QueryOptions
        {
            StaleTime = specific.StaleTime ?? fallback.StaleTime,
            CacheTime = specific.CacheTime ?? fallback.CacheTime,
            RetryCount = specific.RetryCount ?? fallback.RetryCount,
            RetryDelay = specific.RetryDelay ?? fallback.RetryDelay,
            ShouldRetry = specific.ShouldRetry ?? fallback.ShouldRetry,
            RefetchInterval = specific.RefetchInterval ?? fallback.RefetchInterval,
            Enabled = specific.Enabled ?? fallback.Enabled,
            RefetchOnReconnect = specific.RefetchOnReconnect ?? fallback.RefetchOnReconnect,
            RefetchOnFocus = specific.RefetchOnFocus ?? fallback.RefetchOnFocus,
            RefetchOnResume = specific.RefetchOnResume ?? fallback.RefetchOnResume,
            ThrowOnError = specific.ThrowOnError ?? fallback.ThrowOnError,
            CancelInFlightWhenUnobserved = specific.CancelInFlightWhenUnobserved ?? fallback.CancelInFlightWhenUnobserved,
            StructuralSharing = specific.StructuralSharing ?? fallback.StructuralSharing,
            NetworkMode = specific.NetworkMode ?? fallback.NetworkMode,
            Timeout = specific.Timeout ?? fallback.Timeout,
            Priority = specific.Priority ?? fallback.Priority,
            AdaptiveRefetchInterval = specific.AdaptiveRefetchInterval ?? fallback.AdaptiveRefetchInterval,
        };
    }
}
