namespace DotNetQuery.Synchronization;

/// <summary>What a <see cref="QueryEventSynchronizer"/> does when its event queue is full.</summary>
public enum QueueOverflowPolicy
{
    /// <summary>
    /// Drop the event, count it (<see cref="QuerySyncStatistics.Dropped"/>) and schedule a recovery, which reconciles the
    /// cache with the server once the queued events are processed. The transport's receive loop is never blocked. Default.
    /// </summary>
    Resynchronize,

    /// <summary>
    /// Make the transport's handler wait until there is room (backpressure). Nothing is dropped, but a slow cache stalls
    /// the transport - with SignalR that delays its keep-alive handling, so keep the queue large enough for bursts.
    /// </summary>
    Wait,
}

/// <summary>The result of processing one event.</summary>
public enum QueryEventOutcome
{
    /// <summary>At least one mapping applied its cache operations.</summary>
    Applied,

    /// <summary>The event id was already processed within the deduplication window.</summary>
    Duplicate,

    /// <summary>The event is older than one already applied (its entity version or stream sequence).</summary>
    Stale,

    /// <summary>No mapping's <see cref="QueryEventMapping{TEvent}.Where"/> condition accepted the event.</summary>
    Filtered,

    /// <summary>The event belongs to another <see cref="QueryEventMetadata.Scope"/> than the synchronizer's.</summary>
    OutOfScope,

    /// <summary>The event was rejected as malformed: its metadata could not be read, or it has no id while one is required.</summary>
    Rejected,

    /// <summary>Every accepting mapping skipped the event as an echo of a local change (<see cref="QueryEventMapping{TEvent}.IgnoreLocalEchoes"/>).</summary>
    Echo,

    /// <summary>The event skipped positions of its contiguous sequence; it was not applied and a recovery was scheduled.</summary>
    Gap,

    /// <summary>A mapping failed; the queries it targets were invalidated to reconcile them with the server.</summary>
    Failed,

    /// <summary>The queue was full; the event was dropped and a recovery was scheduled.</summary>
    Dropped,

    /// <summary>No mapping is registered for the event name.</summary>
    NoMapping,

    /// <summary>The synchronizer was disposed (or the caller cancelled) before the event was processed.</summary>
    Cancelled,
}

/// <summary>The synchronization state of a <see cref="QueryEventSynchronizer"/>.</summary>
public enum QuerySyncStatus
{
    /// <summary>Not receiving events: no source is connected, or the source stopped or failed permanently.</summary>
    Disconnected,

    /// <summary>The source is establishing its initial connection.</summary>
    Connecting,

    /// <summary>The connection was lost and the source is trying to restore it; events published meanwhile are missed.</summary>
    Reconnecting,

    /// <summary>The connection is back and the source is restoring its server-side subscriptions.</summary>
    Resubscribing,

    /// <summary>Connected; a recovery is reconciling the cache with the server (missed events, a gap, an overflow).</summary>
    Synchronizing,

    /// <summary>Connected, with no recovery pending: the cache follows the server's events.</summary>
    Synchronized,

    /// <summary>A recovery failed unexpectedly (even its fallback invalidation). Call <see cref="QueryEventSynchronizer.ResynchronizeAsync"/> to retry.</summary>
    RecoveryFailed,
}

/// <summary>A transition of <see cref="QueryEventSynchronizer.Status"/>.</summary>
/// <param name="Status">The new status.</param>
/// <param name="Previous">The previous status.</param>
/// <param name="Error">The error that caused the transition, if any.</param>
public readonly record struct QuerySyncStatusChange(QuerySyncStatus Status, QuerySyncStatus Previous, Exception? Error = null);

/// <summary>Why a recovery ran.</summary>
public enum QuerySyncRecoveryReason
{
    /// <summary>The source connected for the first time (<see cref="QueryEventSyncOptions.RecoverOnConnect"/>).</summary>
    Connected,

    /// <summary>The source reconnected after losing its connection; events published meanwhile were missed.</summary>
    Reconnected,

    /// <summary>An event skipped positions of a contiguous sequence.</summary>
    SequenceGap,

    /// <summary>The queue overflowed and events were dropped.</summary>
    QueueOverflow,

    /// <summary><see cref="QueryEventSynchronizer.ResynchronizeAsync"/> was called.</summary>
    Manual,
}

/// <summary>Options for a <see cref="QueryEventSynchronizer"/>.</summary>
public sealed record QueryEventSyncOptions
{
    /// <summary>A name for diagnostics, metrics and DevTools. Defaults to <c>"default"</c>.</summary>
    public string Name { get; init; } = "default";

    /// <summary>
    /// How many received events may wait for processing. Defaults to 10,000. When full, <see cref="OverflowPolicy"/>
    /// applies.
    /// </summary>
    public int QueueCapacity { get; init; } = 10_000;

    /// <summary>What to do when the queue is full. Defaults to <see cref="QueueOverflowPolicy.Resynchronize"/>.</summary>
    public QueueOverflowPolicy OverflowPolicy { get; init; } = QueueOverflowPolicy.Resynchronize;

    /// <summary>How many processed event ids are remembered for duplicate detection. Defaults to 10,000.</summary>
    public int DeduplicationCapacity { get; init; } = 10_000;

    /// <summary>How long a processed event id is remembered. Defaults to 10 minutes.</summary>
    public TimeSpan DeduplicationWindow { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Whether events without a <see cref="QueryEventMetadata.EventId"/> are rejected (<see cref="QueryEventOutcome.Rejected"/>)
    /// instead of processed without duplicate detection. Defaults to <see langword="false"/>.
    /// </summary>
    public bool RequireEventId { get; init; }

    /// <summary>
    /// Whether every stream's <see cref="QueryEventMetadata.Sequence"/> numbers are contiguous (each event is the previous
    /// one plus one), so a jump is a gap: the event is held back and a recovery runs. With <see langword="false"/> (the
    /// default) sequences need only increase; older events are still discarded.
    /// </summary>
    public bool ContiguousSequences { get; init; }

    /// <summary>How many entities' last applied <see cref="QueryEventMetadata.Version"/> are remembered. Defaults to 10,000.</summary>
    public int VersionTrackingCapacity { get; init; } = 10_000;

    /// <summary>How the cache is reconciled after missed events. Defaults to <see cref="QuerySyncRecovery.Invalidate"/>.</summary>
    public QuerySyncRecovery Recovery { get; init; } = QuerySyncRecovery.Invalidate;

    /// <summary>
    /// Whether to run a recovery when the source connects for the first time, reconciling queries fetched before the
    /// subscription was in place. Defaults to <see langword="true"/>.
    /// </summary>
    public bool RecoverOnConnect { get; init; } = true;

    /// <summary>
    /// Invalidations caused by events are coalesced and issued once the queue drains (so a burst refetches each query
    /// once), or after this many events at the latest. Defaults to 256.
    /// </summary>
    public int MaxBatchSize { get; init; } = 256;

    /// <summary>The initial <see cref="QueryEventSynchronizer.Scope"/>. Defaults to <see langword="null"/> (every scope).</summary>
    public string? Scope { get; init; }

    /// <summary>The clock for the deduplication window. Defaults to the query client's clock.</summary>
    public TimeProvider? TimeProvider { get; init; }

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrEmpty(Name, nameof(Name));
        ArgumentOutOfRangeException.ThrowIfLessThan(QueueCapacity, 1, nameof(QueueCapacity));
        ArgumentOutOfRangeException.ThrowIfNegative(DeduplicationCapacity, nameof(DeduplicationCapacity));
        ArgumentOutOfRangeException.ThrowIfNegative(VersionTrackingCapacity, nameof(VersionTrackingCapacity));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBatchSize, 1, nameof(MaxBatchSize));
        ArgumentNullException.ThrowIfNull(Recovery, nameof(Recovery));
        if (DeduplicationWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(DeduplicationWindow), "The deduplication window must be positive.");
        }
    }
}

/// <summary>A point-in-time view of a <see cref="QueryEventSynchronizer"/>, for diagnostics and DevTools.</summary>
public sealed record QuerySyncStatistics
{
    /// <summary>The synchronizer's <see cref="QueryEventSyncOptions.Name"/>.</summary>
    public required string Name { get; init; }

    /// <summary>The current status.</summary>
    public required QuerySyncStatus Status { get; init; }

    /// <summary>The current scope, if any.</summary>
    public string? Scope { get; init; }

    /// <summary>The event names with at least one mapping, and how many mappings each has.</summary>
    public required IReadOnlyDictionary<string, int> Mappings { get; init; }

    /// <summary>The keys a recovery invalidates (prefixes).</summary>
    public required IReadOnlyList<QueryKey> CoveredKeys { get; init; }

    /// <summary>Events received from the source or posted.</summary>
    public long Received { get; init; }

    /// <summary>Events applied by at least one mapping.</summary>
    public long Applied { get; init; }

    /// <summary>Events discarded as duplicates.</summary>
    public long Duplicates { get; init; }

    /// <summary>Events discarded as older than applied ones.</summary>
    public long Stale { get; init; }

    /// <summary>Events no mapping accepted, or of another scope.</summary>
    public long Filtered { get; init; }

    /// <summary>Malformed events.</summary>
    public long Rejected { get; init; }

    /// <summary>Events skipped as echoes of local changes.</summary>
    public long Echoes { get; init; }

    /// <summary>Sequence gaps detected.</summary>
    public long Gaps { get; init; }

    /// <summary>Events whose mapping failed (the affected queries were invalidated instead).</summary>
    public long Failed { get; init; }

    /// <summary>Events dropped because the queue was full.</summary>
    public long Dropped { get; init; }

    /// <summary>Events waiting in the queue.</summary>
    public int QueueDepth { get; init; }

    /// <summary>Recoveries run.</summary>
    public long Recoveries { get; init; }

    /// <summary>Recoveries whose strategy failed or reported missing history, so the fallback invalidation ran.</summary>
    public long RecoveryFallbacks { get; init; }

    /// <summary>Whether a recovery is scheduled or running.</summary>
    public bool RecoveryPending { get; init; }

    /// <summary>The last applied sequence of each stream (the default stream is the empty string).</summary>
    public required IReadOnlyDictionary<string, long> Checkpoints { get; init; }

    /// <summary>When the last event was received.</summary>
    public DateTimeOffset? LastEventAt { get; init; }

    /// <summary>When the synchronizer last became <see cref="QuerySyncStatus.Synchronized"/>.</summary>
    public DateTimeOffset? LastSynchronizedAt { get; init; }

    /// <summary>The most recent errors (mapping failures, recovery failures, source errors), newest last.</summary>
    public required IReadOnlyList<QuerySyncError> RecentErrors { get; init; }
}

/// <summary>An error recorded by a <see cref="QueryEventSynchronizer"/>.</summary>
/// <param name="Timestamp">When it happened.</param>
/// <param name="EventName">The event being processed, or <see langword="null"/> for recovery and connection errors.</param>
/// <param name="Message">The exception message (event payloads are never recorded).</param>
/// <param name="ErrorType">The exception type name.</param>
public sealed record QuerySyncError(DateTimeOffset Timestamp, string? EventName, string Message, string ErrorType);
