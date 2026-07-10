namespace DotNetQuery;

/// <summary>
/// Client-wide default options applied to every query unless overridden by a
/// per-query <see cref="QueryOptions"/>.
/// </summary>
public sealed class QueryClientOptions
{
    /// <summary>Default freshness window for fetched data. Defaults to zero (always stale).</summary>
    public TimeSpan DefaultStaleTime { get; set; } = TimeSpan.Zero;

    /// <summary>Default retention window for inactive queries. Defaults to 5 minutes.</summary>
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
    /// Interval at which the background garbage collector sweeps the cache for expired inactive
    /// queries. Defaults to 1 minute.
    /// </summary>
    public TimeSpan GarbageCollectionInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Resolves the effective options for a query by layering per-query overrides over these defaults.
    /// </summary>
    internal ResolvedQueryOptions Resolve(QueryOptions? options) => new(
        StaleTime: options?.StaleTime ?? DefaultStaleTime,
        CacheTime: options?.CacheTime ?? DefaultCacheTime,
        RetryCount: options?.RetryCount ?? DefaultRetryCount,
        RetryDelay: options?.RetryDelay ?? DefaultRetryDelay,
        RefetchInterval: options?.RefetchInterval,
        Enabled: options?.Enabled ?? true,
        RefetchOnReconnect: options?.RefetchOnReconnect ?? DefaultRefetchOnReconnect,
        RefetchOnFocus: options?.RefetchOnFocus ?? DefaultRefetchOnFocus,
        RefetchOnResume: options?.RefetchOnResume ?? DefaultRefetchOnResume,
        ThrowOnError: options?.ThrowOnError ?? DefaultThrowOnError);
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
    bool ThrowOnError);
