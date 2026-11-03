using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace DotNetQuery.DevTools;

/// <summary>
/// A framework-agnostic developer-tools model over an <see cref="IQueryClient"/>. Provides live cache
/// inspection (including the cached data itself), a rolling diagnostic event log with timings, a mutation
/// history, aggregate statistics, manual cache actions, and a JSON diagnostics export. Any UI (Blazor, WPF,
/// a console) can render this model.
/// </summary>
/// <remarks>
/// The data previews and the JSON export serialize arbitrary types by reflection, so they are annotated as
/// not trim/AOT-safe. Inspection, statistics, the event log and the cache actions are safe everywhere.
/// </remarks>
public sealed class QueryDevTools : IDisposable
{
    private const string ReflectionMessage =
        "DevTools previews and exports serialize arbitrary cached values by reflection, which may require types that cannot be statically analyzed.";

    private const string DynamicCodeMessage =
        "DevTools previews and exports serialize arbitrary cached values by reflection, which may require runtime code generation.";

    private static readonly JsonSerializerOptions PreviewJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly IQueryClient _client;
    private readonly int _eventLogCapacity;
    private readonly int _mutationHistoryCapacity;
    private readonly object _gate = new();
    private readonly Queue<DotNetQueryEvent> _events;
    private readonly LinkedList<MutationRecord> _mutations = new();
    private readonly Dictionary<long, LinkedListNode<MutationRecord>> _mutationsById = [];
    private readonly Action<DotNetQueryEvent> _handler;
    private readonly Action<QueryCacheChange> _cacheHandler;

    private readonly Dictionary<QueryKey, ProfileAccumulator> _profiles = [];
    private readonly List<DevToolsSnapshot> _snapshots = [];
    private Persistence.PersistentMutationQueue? _queue;
    private long _fetchesStarted;
    private long _fetchesSucceeded;
    private long _fetchesFailed;
    private long _fetchesCancelled;
    private long _mutationsStarted;
    private long _mutationsCompleted;
    private long _mutationsFailed;
    private double _totalDurationMs;
    private long _durationSamples;

    /// <summary>Creates a DevTools model attached to a client and begins recording events.</summary>
    /// <param name="client">The client to inspect.</param>
    /// <param name="eventLogCapacity">Maximum number of recent events retained (oldest dropped).</param>
    /// <param name="mutationHistoryCapacity">Maximum number of mutations retained in the history (oldest dropped).</param>
    public QueryDevTools(IQueryClient client, int eventLogCapacity = 500, int mutationHistoryCapacity = 100)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _eventLogCapacity = eventLogCapacity > 0 ? eventLogCapacity : throw new ArgumentOutOfRangeException(nameof(eventLogCapacity));
        _mutationHistoryCapacity = mutationHistoryCapacity > 0 ? mutationHistoryCapacity : throw new ArgumentOutOfRangeException(nameof(mutationHistoryCapacity));
        _events = new Queue<DotNetQueryEvent>(Math.Min(eventLogCapacity, 64));
        _handler = Record;
        _cacheHandler = _ => RaiseChanged();
        _client.DiagnosticEvent += _handler;
        _client.Cache.Changed += _cacheHandler;
    }

    /// <summary>
    /// Raised whenever a diagnostic event is recorded or the cache changes, for live UI refresh. May be
    /// raised frequently and on any thread - UIs should marshal and coalesce.
    /// </summary>
    public event Action? Changed;

    /// <summary>Returns a snapshot describing every query currently in the cache, ordered by key.</summary>
    public IReadOnlyList<QueryDescriptor> InspectQueries() =>
        _client.Cache.Describe().OrderBy(d => d.Key.ToString(), StringComparer.Ordinal).ToArray();

    /// <summary>Returns the recorded events, oldest first.</summary>
    public IReadOnlyList<DotNetQueryEvent> RecentEvents()
    {
        lock (_gate)
        {
            return _events.ToArray();
        }
    }

    /// <summary>Returns the recorded mutations, most recent first (pending mutations included).</summary>
    public IReadOnlyList<MutationRecord> RecentMutations()
    {
        lock (_gate)
        {
            return _mutations.Reverse().ToArray();
        }
    }

    /// <summary>
    /// Renders the cached data of <paramref name="key"/> as indented JSON for display, or
    /// <see langword="null"/> when the query has no data. Falls back to <see cref="object.ToString"/> for
    /// data that cannot be serialized.
    /// </summary>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(DynamicCodeMessage)]
    public string? PreviewData(QueryKey key, JsonSerializerOptions? jsonOptions = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        var descriptor = _client.Cache.Describe(key);
        return descriptor is { HasData: true } ? Preview(descriptor.Data, jsonOptions) : null;
    }

    /// <summary>Renders an arbitrary value (for example mutation variables) as indented JSON for display.</summary>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(DynamicCodeMessage)]
    public static string Preview(object? value, JsonSerializerOptions? jsonOptions = null)
    {
        if (value is null)
        {
            return "null";
        }

        try
        {
            return JsonSerializer.Serialize(value, value.GetType(), jsonOptions ?? PreviewJson);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or JsonException or ArgumentException)
        {
            return value.ToString() ?? value.GetType().Name;
        }
    }

    /// <summary>Computes aggregate statistics from the current cache and recorded activity.</summary>
    public QueryDevToolsStats GetStats()
    {
        var descriptors = _client.Cache.Describe();
        var statusCounts = new Dictionary<QueryStatus, int>();
        var active = 0;
        var stale = 0;
        var fetching = 0;
        foreach (var d in descriptors)
        {
            statusCounts[d.Status] = statusCounts.GetValueOrDefault(d.Status) + 1;
            if (d.IsActive)
            {
                active++;
            }

            if (d.IsStale)
            {
                stale++;
            }

            if (d.Status is QueryStatus.Loading or QueryStatus.Refetching)
            {
                fetching++;
            }
        }

        lock (_gate)
        {
            return new QueryDevToolsStats
            {
                TotalQueries = descriptors.Count,
                ActiveQueries = active,
                StaleQueries = stale,
                FetchingQueries = fetching,
                StatusCounts = statusCounts,
                FetchesStarted = _fetchesStarted,
                FetchesSucceeded = _fetchesSucceeded,
                FetchesFailed = _fetchesFailed,
                FetchesCancelled = _fetchesCancelled,
                MutationsStarted = _mutationsStarted,
                MutationsCompleted = _mutationsCompleted,
                MutationsFailed = _mutationsFailed,
                PendingMutations = _mutations.Count(m => m.Status is MutationStatus.Pending),
                AverageQueryDurationMs = _durationSamples == 0 ? 0 : _totalDurationMs / _durationSamples,
            };
        }
    }

    /// <summary>
    /// Returns the recorded lifecycle of one query, oldest first: fetch starts, retries, completions, failures,
    /// cancellations, invalidations and observer errors - why it fetched, refetched, failed or became stale.
    /// </summary>
    public IReadOnlyList<DotNetQueryEvent> Timeline(QueryKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            return _events.Where(e => e switch
            {
                QueryStartedEvent q => q.Key == key,
                QueryCompletedEvent q => q.Key == key,
                QueryFailedEvent q => q.Key == key,
                QueryCancelledEvent q => q.Key == key,
                QueryRetryEvent q => q.Key == key,
                QueryObserverErrorEvent q => q.Key == key,
                QueryInvalidatedEvent q => q.Keys.Contains(key),
                OptimisticUpdateAppliedEvent o => o.Keys.Contains(key),
                OptimisticUpdateRolledBackEvent o => o.Restored.Contains(key) || o.Refetched.Contains(key),
                _ => false,
            }).ToArray();
        }
    }

    /// <summary>Returns per-query fetch statistics (fetches, failures, retries, cancellations, durations), slowest first.</summary>
    public IReadOnlyList<QueryProfile> ProfileQueries()
    {
        lock (_gate)
        {
            return _profiles.Select(p => p.Value.ToProfile(p.Key)).OrderByDescending(p => p.AverageDurationMs).ToArray();
        }
    }

    /// <summary>Builds the hierarchy of cached query keys (one node per key prefix), for a tree view.</summary>
    public IReadOnlyList<QueryKeyNode> KeyTree()
    {
        var queries = InspectQueries();
        return Build(queries, depth: 0, prefix: null);

        static IReadOnlyList<QueryKeyNode> Build(IReadOnlyList<QueryDescriptor> queries, int depth, QueryKey? prefix)
        {
            return queries
                .Where(q => q.Key.Count > depth)
                .GroupBy(q => q.Key[depth], SegmentComparer.Instance)
                .Select(group =>
                {
                    var key = prefix is null ? QueryKey.Create(group.Key) : prefix.Append(group.Key);
                    var members = group.ToArray();
                    return new QueryKeyNode
                    {
                        Key = key,
                        Segment = QueryKey.Create(group.Key).ToString()[1..^1],
                        Query = members.FirstOrDefault(q => q.Key.Count == depth + 1),
                        Children = Build(members, depth + 1, key),
                        QueryCount = members.Length,
                    };
                })
                .OrderBy(n => n.Segment, StringComparer.Ordinal)
                .ToArray();
        }
    }

    /// <summary>Captures the whole cache (see <see cref="QueryCache.CreateSnapshot"/>) into the snapshot list, newest last.</summary>
    public DevToolsSnapshot TakeSnapshot(string? label = null)
    {
        var snapshot = _client.Cache.CreateSnapshot();
        var entry = new DevToolsSnapshot(label ?? snapshot.CreatedAt.ToLocalTime().ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture), snapshot);
        lock (_gate)
        {
            _snapshots.Add(entry);
            if (_snapshots.Count > 20)
            {
                _snapshots.RemoveAt(0);
            }
        }

        RaiseChanged();
        return entry;
    }

    /// <summary>Gets the recorded snapshots, oldest first.</summary>
    public IReadOnlyList<DevToolsSnapshot> Snapshots
    {
        get { lock (_gate) { return _snapshots.ToArray(); } }
    }

    /// <summary>Time travel: restores the cache to a recorded snapshot exactly (queries added since are removed or reset).</summary>
    public int RestoreSnapshot(DevToolsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Snapshot.Restore(removeOthers: true);
    }

    /// <summary>Resets a query to its initial state and refetches it when observed (controlled state editing).</summary>
    public Task ResetAsync(QueryKey key, CancellationToken cancellationToken = default) =>
        _client.ResetQueriesAsync(QueryFilter.ForKey(key, exact: true), cancellationToken);

    /// <summary>
    /// Attaches a durable offline queue so its pending and dead-lettered mutations can be inspected and managed
    /// (<see cref="QueuedMutations"/>, <see cref="DeadLetters"/>).
    /// </summary>
    public void AttachQueue(Persistence.PersistentMutationQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        _queue = queue;
        queue.PendingChanged += RaiseChanged;
        RaiseChanged();
    }

    /// <summary>Gets the attached queue (see <see cref="AttachQueue"/>), if any.</summary>
    public Persistence.PersistentMutationQueue? Queue => _queue;

    /// <summary>Gets the attached queue's pending mutations, oldest first (empty without a queue).</summary>
    public IReadOnlyList<Persistence.PendingMutation> QueuedMutations => _queue?.Pending ?? [];

    /// <summary>Gets the attached queue's dead letters (empty without a queue).</summary>
    public IReadOnlyList<Persistence.DeadLetterMutation> DeadLetters => _queue?.DeadLetters ?? [];

    /// <summary>Manually invalidates a query hierarchy.</summary>
    public Task InvalidateAsync(QueryKey key, CancellationToken cancellationToken = default) =>
        _client.InvalidateQueriesAsync(key, cancellationToken);

    /// <summary>Manually forces a refetch of a single query.</summary>
    public Task RefetchAsync(QueryKey key, CancellationToken cancellationToken = default) =>
        _client.RefetchQueryAsync(key, cancellationToken);

    /// <summary>Manually cancels a query's in-flight fetch.</summary>
    public Task CancelAsync(QueryKey key, CancellationToken cancellationToken = default) =>
        _client.CancelQueriesAsync(k => k == key, cancellationToken);

    /// <summary>Manually removes a query from the cache.</summary>
    public bool RemoveQuery(QueryKey key) => _client.RemoveQuery(key);

    /// <summary>Clears the recorded event log, mutation history and running tallies (does not touch the cache).</summary>
    public void ClearLog()
    {
        lock (_gate)
        {
            _events.Clear();
            _mutations.Clear();
            _mutationsById.Clear();
            _profiles.Clear();
            _fetchesStarted = _fetchesSucceeded = _fetchesFailed = _fetchesCancelled = 0;
            _mutationsStarted = _mutationsCompleted = _mutationsFailed = 0;
            _totalDurationMs = 0;
            _durationSamples = 0;
        }

        RaiseChanged();
    }

    /// <summary>Exports the current inspection state, stats, mutation history, and event log as indented JSON.</summary>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(DynamicCodeMessage)]
    public string ExportJson(JsonSerializerOptions? jsonOptions = null)
    {
        var options = jsonOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var export = new
        {
            Stats = GetStats(),
            Queries = InspectQueries().Select(ToExport).ToArray(),
            Mutations = RecentMutations().Select(ToExport).ToArray(),
            Profiles = ProfileQueries().Select(p => new
            {
                Key = p.Key.ToString(),
                p.Fetches,
                p.Failures,
                p.Retries,
                p.Cancellations,
                p.AverageDurationMs,
                p.MaxDurationMs,
            }).ToArray(),
            Events = RecentEvents().Select(ToExport).ToArray(),
        };
        return JsonSerializer.Serialize(export, options);
    }

    private void Record(DotNetQueryEvent @event)
    {
        lock (_gate)
        {
            _events.Enqueue(@event);
            while (_events.Count > _eventLogCapacity)
            {
                _events.Dequeue();
            }

            switch (@event)
            {
                case QueryStartedEvent s:
                    _fetchesStarted++;
                    Profile(s.Key).Fetches++;
                    break;
                case QueryCompletedEvent c:
                    _fetchesSucceeded++;
                    _totalDurationMs += c.Duration.TotalMilliseconds;
                    _durationSamples++;
                    Profile(c.Key).Settled(c.Duration);
                    break;
                case QueryFailedEvent f:
                    _fetchesFailed++;
                    _totalDurationMs += f.Duration.TotalMilliseconds;
                    _durationSamples++;
                    Profile(f.Key).Failures++;
                    Profile(f.Key).Settled(f.Duration);
                    break;
                case QueryCancelledEvent x:
                    _fetchesCancelled++;
                    Profile(x.Key).Cancellations++;
                    break;
                case QueryRetryEvent r:
                    Profile(r.Key).Retries++;
                    break;
                case OptimisticUpdateAppliedEvent o when _mutationsById.TryGetValue(o.MutationId, out var applied):
                    applied.Value = applied.Value with { OptimisticKeys = o.Keys };
                    break;
                case OptimisticUpdateRolledBackEvent o when _mutationsById.TryGetValue(o.MutationId, out var rolledBack):
                    rolledBack.Value = rolledBack.Value with { RolledBackKeys = o.Restored, RefetchedKeys = o.Refetched };
                    break;
                case MutationStartedEvent m:
                    _mutationsStarted++;
                    AddMutationLocked(new MutationRecord
                    {
                        MutationId = m.MutationId,
                        MutationKey = m.MutationKey,
                        Variables = m.Variables,
                        Status = MutationStatus.Pending,
                        SubmittedAt = m.Timestamp,
                    });
                    break;
                case MutationCompletedEvent m:
                    _mutationsCompleted++;
                    if (!m.Succeeded)
                    {
                        _mutationsFailed++;
                    }

                    if (_mutationsById.TryGetValue(m.MutationId, out var node))
                    {
                        node.Value = node.Value with
                        {
                            Status = m.Succeeded ? MutationStatus.Success : MutationStatus.Error,
                            Duration = m.Duration,
                            ErrorMessage = m.Error?.Message,
                            FailureCount = m.FailureCount,
                        };
                    }

                    break;
            }
        }

        RaiseChanged();
    }

    // Must hold _gate.
    private ProfileAccumulator Profile(QueryKey key)
    {
        if (!_profiles.TryGetValue(key, out var profile))
        {
            if (_profiles.Count >= _eventLogCapacity)
            {
                _profiles.Remove(_profiles.Keys.First());
            }

            _profiles[key] = profile = new ProfileAccumulator();
        }

        return profile;
    }

    private sealed class ProfileAccumulator
    {
        private int _settled;
        private double _totalMs;
        private double _maxMs;

        public int Fetches { get; set; }

        public int Failures { get; set; }

        public int Retries { get; set; }

        public int Cancellations { get; set; }

        public void Settled(TimeSpan duration)
        {
            _settled++;
            _totalMs += duration.TotalMilliseconds;
            _maxMs = Math.Max(_maxMs, duration.TotalMilliseconds);
        }

        public QueryProfile ToProfile(QueryKey key) => new()
        {
            Key = key,
            Fetches = Fetches,
            Failures = Failures,
            Retries = Retries,
            Cancellations = Cancellations,
            AverageDurationMs = _settled == 0 ? 0 : _totalMs / _settled,
            MaxDurationMs = _maxMs,
        };
    }

    // Groups key segments the way QueryKey compares them.
    private sealed class SegmentComparer : IEqualityComparer<object?>
    {
        public static SegmentComparer Instance { get; } = new();

        public new bool Equals(object? x, object? y) => QueryKey.Create(x) == QueryKey.Create(y);

        public int GetHashCode(object? obj) => QueryKey.Create(obj).GetHashCode();
    }

    private void AddMutationLocked(MutationRecord record)
    {
        _mutationsById[record.MutationId] = _mutations.AddLast(record);
        while (_mutations.Count > _mutationHistoryCapacity)
        {
            _mutationsById.Remove(_mutations.First!.Value.MutationId);
            _mutations.RemoveFirst();
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception)
        {
            // A faulty UI subscriber must not break the client.
        }
    }

    private static object ToExport(QueryDescriptor d) => new
    {
        Key = d.Key.ToString(),
        Status = d.Status.ToString(),
        d.DataTypeName,
        d.HasData,
        d.IsStale,
        d.ObserverCount,
        d.FetchCount,
        d.FailureCount,
        d.ErrorMessage,
        d.DataUpdatedAt,
        d.LastActivityAt,
        CacheTimeSeconds = d.CacheTime == TimeSpan.MaxValue ? -1 : d.CacheTime.TotalSeconds,
        StaleTimeSeconds = d.StaleTime is not { } s ? (double?)null : s == TimeSpan.MaxValue ? -1 : s.TotalSeconds,
    };

    private static object ToExport(MutationRecord m) => new
    {
        m.MutationId,
        MutationKey = m.MutationKey?.ToString(),
        Status = m.Status.ToString(),
        m.SubmittedAt,
        DurationMs = m.Duration?.TotalMilliseconds,
        m.FailureCount,
        m.ErrorMessage,
    };

    private static object ToExport(DotNetQueryEvent e) => e switch
    {
        QueryStartedEvent q => new EventExport("QueryStarted", e.Timestamp, q.Key.ToString(), null, null, null),
        QueryCompletedEvent q => new EventExport("QueryCompleted", e.Timestamp, q.Key.ToString(), q.Duration.TotalMilliseconds, true, null),
        QueryFailedEvent q => new EventExport("QueryFailed", e.Timestamp, q.Key.ToString(), q.Duration.TotalMilliseconds, false, q.Error.Message),
        QueryCancelledEvent q => new EventExport("QueryCancelled", e.Timestamp, q.Key.ToString(), q.Duration.TotalMilliseconds, null, null),
        QueryInvalidatedEvent q => new EventExport("QueryInvalidated", e.Timestamp, string.Join(", ", q.Keys), null, null, null),
        QueryObserverErrorEvent q => new EventExport("ObserverError", e.Timestamp, q.Key.ToString(), null, null, q.Error.Message),
        QueryRetryEvent q => new EventExport("QueryRetry", e.Timestamp, q.Key.ToString(), null, null, q.Error.Message),
        OptimisticUpdateAppliedEvent o => new EventExport("OptimisticApplied", e.Timestamp, string.Join(", ", o.Keys), null, null, null),
        OptimisticUpdateRolledBackEvent o => new EventExport("OptimisticRolledBack", e.Timestamp, string.Join(", ", o.Restored.Concat(o.Refetched)), null, null, null),
        MutationStartedEvent m => new EventExport("MutationStarted", e.Timestamp, m.MutationKey?.ToString(), null, null, null),
        MutationCompletedEvent m => new EventExport("MutationCompleted", e.Timestamp, m.MutationKey?.ToString(), m.Duration.TotalMilliseconds, m.Succeeded, m.Error?.Message),
        _ => new EventExport(e.GetType().Name, e.Timestamp, null, null, null, null),
    };

    private sealed record EventExport(
        string Type,
        DateTimeOffset Timestamp,
        string? Key,
        double? DurationMs,
        bool? Succeeded,
        string? Error);

    /// <summary>Stops recording events and cache changes.</summary>
    public void Dispose()
    {
        _client.DiagnosticEvent -= _handler;
        _client.Cache.Changed -= _cacheHandler;
        if (_queue is not null)
        {
            _queue.PendingChanged -= RaiseChanged;
        }
    }
}
