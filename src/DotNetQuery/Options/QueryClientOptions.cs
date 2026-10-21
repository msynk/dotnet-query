namespace DotNetQuery;

/// <summary>
/// Client-wide default options applied to every query unless overridden by a
/// per-query <see cref="QueryOptions"/>.
/// </summary>
public sealed class QueryClientOptions
{
    /// <summary>
    /// Default freshness window for fetched data. Defaults to zero (always stale). Use
    /// <see cref="Timeout.InfiniteTimeSpan"/> for data that never goes stale on its own.
    /// </summary>
    public TimeSpan DefaultStaleTime { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Default retention window for inactive queries. Defaults to 5 minutes. Use
    /// <see cref="Timeout.InfiniteTimeSpan"/> to never garbage-collect inactive queries.
    /// </summary>
    public TimeSpan DefaultCacheTime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Default number of automatic retries after a failed fetch. Defaults to 3.</summary>
    public int DefaultRetryCount { get; set; } = 3;

    /// <summary>Default retry back-off strategy. Defaults to exponential with jitter.</summary>
    public RetryDelayStrategy DefaultRetryDelay { get; set; } = RetryPolicy.Default;

    /// <summary>Default value for <see cref="QueryOptions.RefetchOnReconnect"/>. Defaults to <see langword="true"/>.</summary>
    public bool DefaultRefetchOnReconnect { get; set; } = true;

    /// <summary>Default value for <see cref="QueryOptions.RefetchOnFocus"/>. Defaults to <see langword="true"/>.</summary>
    public bool DefaultRefetchOnFocus { get; set; } = true;

    /// <summary>Default value for <see cref="QueryOptions.RefetchOnResume"/>. Defaults to <see langword="true"/>.</summary>
    public bool DefaultRefetchOnResume { get; set; } = true;

    /// <summary>Default value for <see cref="QueryOptions.ThrowOnError"/>. Defaults to <see langword="false"/>.</summary>
    public bool DefaultThrowOnError { get; set; }

    /// <summary>
    /// Default value for <see cref="QueryOptions.CancelInFlightWhenUnobserved"/>. Defaults to
    /// <see langword="false"/> (an in-flight fetch runs to completion even after its last observer leaves).
    /// </summary>
    public bool DefaultCancelInFlightWhenUnobserved { get; set; }

    /// <summary>
    /// Interval at which the background garbage collector sweeps the cache for expired inactive
    /// queries. Defaults to 1 minute.
    /// </summary>
    public TimeSpan GarbageCollectionInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Resolves the effective options for a query by layering per-query overrides over these defaults.
    /// </summary>
    internal ResolvedQueryOptions Resolve(QueryOptions? options) => new(
        StaleTime: NormalizeDuration(options?.StaleTime ?? DefaultStaleTime),
        CacheTime: NormalizeDuration(options?.CacheTime ?? DefaultCacheTime),
        RetryCount: Math.Max(0, options?.RetryCount ?? DefaultRetryCount),
        RetryDelay: options?.RetryDelay ?? DefaultRetryDelay ?? RetryPolicy.Default,
        RefetchInterval: options?.RefetchInterval is { } interval && interval > TimeSpan.Zero ? interval : null,
        Enabled: options?.Enabled ?? true,
        RefetchOnReconnect: options?.RefetchOnReconnect ?? DefaultRefetchOnReconnect,
        RefetchOnFocus: options?.RefetchOnFocus ?? DefaultRefetchOnFocus,
        RefetchOnResume: options?.RefetchOnResume ?? DefaultRefetchOnResume,
        ThrowOnError: options?.ThrowOnError ?? DefaultThrowOnError,
        CancelInFlightWhenUnobserved: options?.CancelInFlightWhenUnobserved ?? DefaultCancelInFlightWhenUnobserved);

    /// <summary>
    /// Maps <see cref="Timeout.InfiniteTimeSpan"/> to <see cref="TimeSpan.MaxValue"/> (never elapses) and
    /// clamps other negative durations to zero.
    /// </summary>
    internal static TimeSpan NormalizeDuration(TimeSpan value) =>
        value == Timeout.InfiniteTimeSpan ? TimeSpan.MaxValue : value < TimeSpan.Zero ? TimeSpan.Zero : value;
}

/// <summary>Fully materialized options with all defaults applied. Internal to the client.</summary>
internal readonly record struct ResolvedQueryOptions(
    TimeSpan StaleTime,
    TimeSpan CacheTime,
    int RetryCount,
    RetryDelayStrategy RetryDelay,
    TimeSpan? RefetchInterval,
    bool Enabled,
    bool RefetchOnReconnect,
    bool RefetchOnFocus,
    bool RefetchOnResume,
    bool ThrowOnError,
    bool CancelInFlightWhenUnobserved);
