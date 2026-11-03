namespace DotNetQuery;

/// <summary>
/// Client-wide default options applied to every query unless overridden by a
/// per-query <see cref="QueryOptions"/>.
/// </summary>
public sealed class QueryClientOptions
{
    private readonly object _defaultsGate = new();
    private readonly List<(QueryKey Prefix, QueryOptions Options)> _queryDefaults = [];
    private readonly List<(QueryKey Source, QueryKey Dependent)> _invalidationDependencies = [];
    private readonly List<(QueryKey? Prefix, MutationDefaults Defaults)> _mutationDefaults = [];

    /// <summary>Sets defaults for every mutation (see <see cref="MutationDefaults"/>), replacing earlier global ones.</summary>
    /// <returns>These options, for chaining.</returns>
    public QueryClientOptions SetMutationDefaults(MutationDefaults defaults) => SetMutationDefaultsCore(null, defaults);

    /// <summary>
    /// Sets defaults for mutations whose <c>MutationKey</c> starts with <paramref name="mutationKeyPrefix"/>, replacing
    /// earlier defaults for the same prefix. More specific prefixes win over shorter ones and over global defaults.
    /// </summary>
    /// <returns>These options, for chaining.</returns>
    public QueryClientOptions SetMutationDefaults(QueryKey mutationKeyPrefix, MutationDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(mutationKeyPrefix);
        return SetMutationDefaultsCore(mutationKeyPrefix, defaults);
    }

    private QueryClientOptions SetMutationDefaultsCore(QueryKey? prefix, MutationDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        lock (_defaultsGate)
        {
            _mutationDefaults.RemoveAll(d => d.Prefix == prefix);
            _mutationDefaults.Add((prefix, defaults));
        }

        return this;
    }

    /// <summary>Returns the merged defaults that apply to a mutation with <paramref name="mutationKey"/> (or none).</summary>
    public MutationDefaults? GetMutationDefaults(QueryKey? mutationKey)
    {
        lock (_defaultsGate)
        {
            MutationDefaults? merged = null;
            foreach (var (_, defaults) in _mutationDefaults
                .Where(d => d.Prefix is null || (mutationKey is not null && d.Prefix.IsPrefixOf(mutationKey)))
                .OrderBy(d => d.Prefix?.Count ?? 0))
            {
                merged = MutationDefaults.Merge(defaults, merged);
            }

            return merged;
        }
    }

    /// <summary>
    /// Whether mutation variables are included in diagnostic events (<see cref="MutationStartedEvent.Variables"/>, and
    /// so the DevTools mutation history). Defaults to <see langword="true"/>; turn it off when variables can contain
    /// secrets or personal data and diagnostics leave the process.
    /// </summary>
    public bool IncludeVariablesInDiagnostics { get; set; } = true;

    /// <summary>
    /// Formats query and mutation keys for OpenTelemetry tags (<c>dotnetquery.query.key</c>,
    /// <c>dotnetquery.mutation.key</c>). Defaults to <see cref="QueryKey.ToString"/>; supply a formatter that redacts or
    /// truncates segments when keys can contain personal data (for example an e-mail address).
    /// </summary>
    public Func<QueryKey, string>? DiagnosticKeyFormatter { get; set; }

    /// <summary>
    /// The most fetch attempts of this client that may run at once (across all queries). Further attempts wait - by
    /// <see cref="QueryOptions.Priority"/>, then in order - for a slot. <see langword="null"/> (the default) is unlimited.
    /// </summary>
    public int? MaxConcurrentFetches { get; set; }

    /// <summary>
    /// An optional gate every fetch attempt must pass (for example an adapter over a
    /// <c>System.Threading.RateLimiting.TokenBucketRateLimiter</c> matching an API's quota; see
    /// <see cref="IFetchRateLimiter"/>). A rejected attempt fails like any fetch error, so retries apply.
    /// </summary>
    public IFetchRateLimiter? FetchRateLimiter { get; set; }

    /// <summary>Interceptors wrapped around every fetch attempt, outermost first (see <see cref="IQueryInterceptor"/>).</summary>
    public IList<IQueryInterceptor> Interceptors { get; } = new List<IQueryInterceptor>();

    /// <summary>
    /// The most inactive (unobserved, idle) queries kept in the cache. When exceeded, the garbage collector evicts the
    /// least recently used inactive queries even before their cache time elapses. <see langword="null"/> (the default)
    /// bounds the cache by time only.
    /// </summary>
    public int? MaxInactiveQueries { get; set; }

    /// <summary>
    /// When <see langword="true"/>, every inactive query is evicted when the process is under memory pressure (memory
    /// load at or above the runtime's high-load threshold after a full garbage collection). Defaults to <see langword="false"/>.
    /// </summary>
    public bool TrimOnMemoryPressure { get; set; }

    /// <summary>
    /// Declares that queries under <paramref name="dependent"/> derive from queries under <paramref name="source"/>:
    /// invalidating any query under <paramref name="source"/> also invalidates (and refetches, when active) everything
    /// under <paramref name="dependent"/>. Dependencies are followed transitively.
    /// </summary>
    /// <example><c>options.AddInvalidationDependency(QueryKey.Create("todos"), QueryKey.Create("stats"));</c></example>
    /// <returns>These options, for chaining.</returns>
    public QueryClientOptions AddInvalidationDependency(QueryKey source, QueryKey dependent)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(dependent);
        lock (_defaultsGate)
        {
            if (!_invalidationDependencies.Contains((source, dependent)))
            {
                _invalidationDependencies.Add((source, dependent));
            }
        }

        return this;
    }

    /// <summary>
    /// Returns the dependent prefixes of the sources that cover <paramref name="key"/> - and, for a requested prefix
    /// (<paramref name="includeNarrowerSources"/>), of the sources it covers (invalidating <c>["todos"]</c> covers a
    /// source declared as <c>["todos", "open"]</c>).
    /// </summary>
    internal IReadOnlyList<QueryKey> GetInvalidationDependents(QueryKey key, bool includeNarrowerSources = false)
    {
        lock (_defaultsGate)
        {
            if (_invalidationDependencies.Count == 0)
            {
                return [];
            }

            return _invalidationDependencies
                .Where(d => d.Source.IsPrefixOf(key) || (includeNarrowerSources && key.IsPrefixOf(d.Source)))
                .Select(d => d.Dependent)
                .ToArray();
        }
    }

    internal bool HasInvalidationDependencies
    {
        get { lock (_defaultsGate) { return _invalidationDependencies.Count > 0; } }
    }

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

    /// <summary>
    /// Default value for <see cref="QueryOptions.ShouldRetry"/>. Defaults to <see langword="null"/>: every
    /// failure is retried until <see cref="DefaultRetryCount"/> is exhausted.
    /// </summary>
    public RetryPredicate? DefaultShouldRetry { get; set; }

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
    /// Default value for <see cref="QueryOptions.StructuralSharing"/>. Defaults to <see langword="false"/>, because
    /// sharing relies on <see cref="object.Equals(object?)"/> describing full value equality, which only your
    /// types can guarantee. Enable it when cached data is made of records, strings, primitives and collections.
    /// </summary>
    public bool DefaultStructuralSharing { get; set; }

    /// <summary>Default value for <see cref="QueryOptions.NetworkMode"/>. Defaults to <see cref="NetworkMode.Online"/>.</summary>
    public NetworkMode DefaultNetworkMode { get; set; } = NetworkMode.Online;

    /// <summary>
    /// Default value for <see cref="MutationOptions{TVariables, TData}.NetworkMode"/>. Defaults to
    /// <see cref="NetworkMode.Always"/> (a mutation started offline runs and fails as before); set
    /// <see cref="NetworkMode.Online"/> to make writes wait for connectivity instead.
    /// </summary>
    public NetworkMode DefaultMutationNetworkMode { get; set; } = NetworkMode.Always;

    /// <summary>
    /// Interval at which the background garbage collector sweeps the cache for expired inactive
    /// queries. Defaults to 1 minute.
    /// </summary>
    public TimeSpan GarbageCollectionInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long a settled mutation run stays in <see cref="QueryCache.Mutations"/> for inspection. Defaults to 5
    /// minutes; <see cref="TimeSpan.Zero"/> drops runs as soon as they settle, <see cref="Timeout.InfiniteTimeSpan"/>
    /// keeps them until <see cref="MutationCache.Clear"/>.
    /// </summary>
    public TimeSpan MutationCacheTime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Sets default options for every query whose key starts with <paramref name="prefix"/> (replacing earlier
    /// defaults for the same prefix). Options are layered from general to specific: client-wide defaults, then the
    /// defaults of each matching prefix from shortest to longest, then the query's own options - each layer only
    /// overrides the properties it sets.
    /// </summary>
    /// <example>
    /// <code>
    /// options.SetQueryDefaults(QueryKey.Create("reference-data"), new QueryOptions { StaleTime = TimeSpan.FromHours(1) });
    /// options.SetQueryDefaults(QueryKey.Create("live"), new QueryOptions { RefetchInterval = TimeSpan.FromSeconds(5) });
    /// </code>
    /// </example>
    /// <returns>These options, for chaining.</returns>
    public QueryClientOptions SetQueryDefaults(QueryKey prefix, QueryOptions defaults)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(defaults);
        lock (_defaultsGate)
        {
            _queryDefaults.RemoveAll(d => d.Prefix == prefix);
            _queryDefaults.Add((prefix, defaults));
        }

        return this;
    }

    /// <summary>
    /// Returns the merged prefix defaults that apply to <paramref name="key"/> (see <see cref="SetQueryDefaults"/>),
    /// or <see langword="null"/> when none match.
    /// </summary>
    public QueryOptions? GetQueryDefaults(QueryKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_defaultsGate)
        {
            if (_queryDefaults.Count == 0)
            {
                return null;
            }

            QueryOptions? merged = null;
            foreach (var (prefix, options) in _queryDefaults.Where(d => d.Prefix.IsPrefixOf(key)).OrderBy(d => d.Prefix.Count))
            {
                merged = QueryOptions.Merge(options, merged);
            }

            return merged;
        }
    }

    /// <summary>Resolves the effective options for the query under <paramref name="key"/>, including prefix defaults.</summary>
    internal ResolvedQueryOptions Resolve(QueryKey key, QueryOptions? options) => Resolve(QueryOptions.Merge(options, GetQueryDefaults(key)));

    /// <summary>
    /// Resolves the effective options for a query by layering per-query overrides over these defaults.
    /// </summary>
    internal ResolvedQueryOptions Resolve(QueryOptions? options) => new(
        StaleTime: NormalizeDuration(options?.StaleTime ?? DefaultStaleTime),
        CacheTime: NormalizeDuration(options?.CacheTime ?? DefaultCacheTime),
        RetryCount: Math.Max(0, options?.RetryCount ?? DefaultRetryCount),
        RetryDelay: options?.RetryDelay ?? DefaultRetryDelay ?? RetryPolicy.Default,
        ShouldRetry: options?.ShouldRetry ?? DefaultShouldRetry,
        RefetchInterval: options?.RefetchInterval is { } interval && interval > TimeSpan.Zero ? interval : null,
        Enabled: options?.Enabled ?? true,
        RefetchOnReconnect: options?.RefetchOnReconnect ?? DefaultRefetchOnReconnect,
        RefetchOnFocus: options?.RefetchOnFocus ?? DefaultRefetchOnFocus,
        RefetchOnResume: options?.RefetchOnResume ?? DefaultRefetchOnResume,
        ThrowOnError: options?.ThrowOnError ?? DefaultThrowOnError,
        CancelInFlightWhenUnobserved: options?.CancelInFlightWhenUnobserved ?? DefaultCancelInFlightWhenUnobserved,
        StructuralSharing: options?.StructuralSharing ?? DefaultStructuralSharing,
        NetworkMode: options?.NetworkMode ?? DefaultNetworkMode,
        Timeout: options?.Timeout is { } timeout && timeout > TimeSpan.Zero && timeout != System.Threading.Timeout.InfiniteTimeSpan ? timeout : null,
        Priority: options?.Priority ?? 0,
        AdaptiveRefetchInterval: options?.AdaptiveRefetchInterval);

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
    RetryPredicate? ShouldRetry,
    TimeSpan? RefetchInterval,
    bool Enabled,
    bool RefetchOnReconnect,
    bool RefetchOnFocus,
    bool RefetchOnResume,
    bool ThrowOnError,
    bool CancelInFlightWhenUnobserved,
    bool StructuralSharing,
    NetworkMode NetworkMode,
    TimeSpan? Timeout,
    int Priority,
    Func<QueryDescriptor, TimeSpan?>? AdaptiveRefetchInterval);
