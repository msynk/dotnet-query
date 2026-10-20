using System.Text.Json;

namespace DotNetQuery.DevTools;

/// <summary>
/// A framework-agnostic developer-tools model over an <see cref="IQueryClient"/>. Provides live cache
/// inspection (including the cached data itself), a rolling diagnostic event log with timings, a mutation
/// history, aggregate statistics, manual cache actions, and a JSON diagnostics export. Any UI (Blazor, WPF,
/// a console) can render this model.
/// </summary>
public sealed class QueryDevTools : IDisposable
{
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
    public string? PreviewData(QueryKey key, JsonSerializerOptions? jsonOptions = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        var descriptor = _client.Cache.Describe(key);
        return descriptor is { HasData: true } ? Preview(descriptor.Data, jsonOptions) : null;
    }

    /// <summary>Renders an arbitrary value (for example mutation variables) as indented JSON for display.</summary>
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
            _fetchesStarted = _fetchesSucceeded = _fetchesFailed = _fetchesCancelled = 0;
            _mutationsStarted = _mutationsCompleted = _mutationsFailed = 0;
            _totalDurationMs = 0;
            _durationSamples = 0;
        }

        RaiseChanged();
    }

    /// <summary>Exports the current inspection state, stats, mutation history, and event log as indented JSON.</summary>
    public string ExportJson(JsonSerializerOptions? jsonOptions = null)
    {
        var options = jsonOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var export = new
        {
            Stats = GetStats(),
            Queries = InspectQueries().Select(ToExport).ToArray(),
            Mutations = RecentMutations().Select(ToExport).ToArray(),
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
                case QueryStartedEvent:
                    _fetchesStarted++;
                    break;
                case QueryCompletedEvent c:
                    _fetchesSucceeded++;
                    _totalDurationMs += c.Duration.TotalMilliseconds;
                    _durationSamples++;
                    break;
                case QueryFailedEvent f:
                    _fetchesFailed++;
                    _totalDurationMs += f.Duration.TotalMilliseconds;
                    _durationSamples++;
                    break;
                case QueryCancelledEvent:
                    _fetchesCancelled++;
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
    }
}
