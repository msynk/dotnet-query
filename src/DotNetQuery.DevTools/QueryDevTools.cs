using System.Text.Json;

namespace DotNetQuery.DevTools;

/// <summary>
/// A framework-agnostic developer-tools model over an <see cref="IQueryClient"/>. Provides live cache
/// inspection, a rolling diagnostic event log with timings, aggregate statistics, manual cache actions,
/// and a JSON diagnostics export. Any UI (Blazor, WPF, a console) can render this model.
/// </summary>
public sealed class QueryDevTools : IDisposable
{
    private readonly IQueryClient _client;
    private readonly int _eventLogCapacity;
    private readonly object _gate = new();
    private readonly Queue<DotNetQueryEvent> _events;
    private readonly Action<DotNetQueryEvent> _handler;

    private long _fetchesStarted;
    private long _fetchesSucceeded;
    private long _fetchesFailed;
    private long _mutationsStarted;
    private long _mutationsCompleted;
    private double _totalDurationMs;
    private long _durationSamples;

    /// <summary>Creates a DevTools model attached to a client and begins recording events.</summary>
    /// <param name="client">The client to inspect.</param>
    /// <param name="eventLogCapacity">Maximum number of recent events retained (oldest dropped).</param>
    public QueryDevTools(IQueryClient client, int eventLogCapacity = 500)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _eventLogCapacity = eventLogCapacity > 0 ? eventLogCapacity : throw new ArgumentOutOfRangeException(nameof(eventLogCapacity));
        _events = new Queue<DotNetQueryEvent>(Math.Min(eventLogCapacity, 64));
        _handler = Record;
        _client.DiagnosticEvent += _handler;
    }

    /// <summary>Raised whenever a new diagnostic event is recorded, for live UI refresh.</summary>
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

    /// <summary>Computes aggregate statistics from the current cache and recorded activity.</summary>
    public QueryDevToolsStats GetStats()
    {
        var descriptors = _client.Cache.Describe();
        var statusCounts = new Dictionary<QueryStatus, int>();
        var active = 0;
        var stale = 0;
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
        }

        lock (_gate)
        {
            return new QueryDevToolsStats
            {
                TotalQueries = descriptors.Count,
                ActiveQueries = active,
                StaleQueries = stale,
                StatusCounts = statusCounts,
                FetchesStarted = _fetchesStarted,
                FetchesSucceeded = _fetchesSucceeded,
                FetchesFailed = _fetchesFailed,
                MutationsStarted = _mutationsStarted,
                MutationsCompleted = _mutationsCompleted,
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

    /// <summary>Manually removes a query from the cache.</summary>
    public bool RemoveQuery(QueryKey key) => _client.RemoveQuery(key);

    /// <summary>Clears the recorded event log and running tallies (does not touch the cache).</summary>
    public void ClearLog()
    {
        lock (_gate)
        {
            _events.Clear();
            _fetchesStarted = _fetchesSucceeded = _fetchesFailed = 0;
            _mutationsStarted = _mutationsCompleted = 0;
            _totalDurationMs = 0;
            _durationSamples = 0;
        }

        Changed?.Invoke();
    }

    /// <summary>Exports the current inspection state, stats, and event log as indented JSON.</summary>
    public string ExportJson(JsonSerializerOptions? jsonOptions = null)
    {
        var options = jsonOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var export = new
        {
            Stats = GetStats(),
            Queries = InspectQueries().Select(ToExport).ToArray(),
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
                case MutationStartedEvent:
                    _mutationsStarted++;
                    break;
                case MutationCompletedEvent:
                    _mutationsCompleted++;
                    break;
            }
        }

        Changed?.Invoke();
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
        CacheTimeSeconds = d.CacheTime.TotalSeconds,
    };

    private static object ToExport(DotNetQueryEvent e) => e switch
    {
        QueryStartedEvent q => new EventExport("QueryStarted", e.Timestamp, q.Key.ToString(), null, null, null),
        QueryCompletedEvent q => new EventExport("QueryCompleted", e.Timestamp, q.Key.ToString(), q.Duration.TotalMilliseconds, true, null),
        QueryFailedEvent q => new EventExport("QueryFailed", e.Timestamp, q.Key.ToString(), q.Duration.TotalMilliseconds, false, q.Error.Message),
        QueryInvalidatedEvent q => new EventExport("QueryInvalidated", e.Timestamp, string.Join(", ", q.Keys), null, null, null),
        MutationStartedEvent => new EventExport("MutationStarted", e.Timestamp, null, null, null, null),
        MutationCompletedEvent m => new EventExport("MutationCompleted", e.Timestamp, null, m.Duration.TotalMilliseconds, m.Succeeded, m.Error?.Message),
        _ => new EventExport(e.GetType().Name, e.Timestamp, null, null, null, null),
    };

    private sealed record EventExport(
        string Type,
        DateTimeOffset Timestamp,
        string? Key,
        double? DurationMs,
        bool? Succeeded,
        string? Error);

    /// <summary>Stops recording events.</summary>
    public void Dispose() => _client.DiagnosticEvent -= _handler;
}
