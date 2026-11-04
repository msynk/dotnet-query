namespace DotNetQuery.Synchronization;

/// <summary>The kind of a <see cref="QuerySyncRecovery"/> strategy.</summary>
public enum QuerySyncRecoveryKind
{
    /// <summary>Invalidate every covered query; active ones refetch. The baseline, always-safe strategy.</summary>
    Invalidate,

    /// <summary>Fetch authoritative state and write it to the cache.</summary>
    Snapshot,

    /// <summary>Replay the missed events from a server-retained history, starting after the last checkpoint.</summary>
    Replay,

    /// <summary>Application-defined reconciliation.</summary>
    Custom,
}

/// <summary>
/// How a <see cref="QueryEventSynchronizer"/> reconciles the cache after events may have been missed: after a reconnect,
/// a sequence gap or a queue overflow. Every strategy other than <see cref="Invalidate"/> falls back to invalidation
/// when it throws or reports that it could not recover (for example because the server no longer retains the history),
/// so a recovery never leaves the cache silently out of date.
/// </summary>
public sealed class QuerySyncRecovery
{
    private QuerySyncRecovery(QuerySyncRecoveryKind kind, Func<QuerySyncRecoveryContext, CancellationToken, Task<bool>>? recover)
    {
        Kind = kind;
        Recover = recover;
    }

    /// <summary>
    /// Invalidates every query covered by the mappings (their static keys and <see cref="QueryEventMapping{TEvent}.Covers"/>):
    /// observed queries refetch now, the others on their next use. Requires nothing from the server. The default.
    /// </summary>
    public static QuerySyncRecovery Invalidate { get; } = new(QuerySyncRecoveryKind.Invalidate, null);

    /// <summary>Gets the kind of strategy.</summary>
    public QuerySyncRecoveryKind Kind { get; }

    internal Func<QuerySyncRecoveryContext, CancellationToken, Task<bool>>? Recover { get; }

    /// <summary>
    /// Reconciles by fetching authoritative state and writing it to the cache (for example one snapshot endpoint for the
    /// whole screen). If the snapshot carries a sequence watermark, record it with
    /// <see cref="QuerySyncRecoveryContext.SetCheckpoint"/> so older events still queued are discarded; otherwise the next
    /// event of each stream starts a new baseline. Falls back to invalidation if <paramref name="snapshot"/> throws.
    /// </summary>
    public static QuerySyncRecovery Snapshot(Func<QuerySyncRecoveryContext, CancellationToken, Task> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new(QuerySyncRecoveryKind.Snapshot, async (context, ct) =>
        {
            await snapshot(context, ct).ConfigureAwait(false);
            return true;
        });
    }

    /// <summary>
    /// Reconciles by replaying the events missed since the last checkpoint - only for servers that retain an event
    /// history and can resume from a sequence or cursor. <paramref name="replay"/> reads
    /// <see cref="QuerySyncRecoveryContext.GetCheckpoint"/>, fetches the missed events and feeds each one through
    /// <see cref="QuerySyncRecoveryContext.ApplyAsync"/> (deduplication and ordering still apply); it returns
    /// <see langword="false"/> when the server cannot replay from that checkpoint (history truncated, unknown cursor), and
    /// the recovery falls back to invalidation. With <see cref="QueryEventSyncOptions.ContiguousSequences"/>, a replay that
    /// leaves a gap also falls back.
    /// </summary>
    public static QuerySyncRecovery Replay(Func<QuerySyncRecoveryContext, CancellationToken, Task<bool>> replay)
    {
        ArgumentNullException.ThrowIfNull(replay);
        return new(QuerySyncRecoveryKind.Replay, replay);
    }

    /// <summary>
    /// Application-defined reconciliation. Return <see langword="false"/> to fall back to invalidation.
    /// </summary>
    public static QuerySyncRecovery Custom(Func<QuerySyncRecoveryContext, CancellationToken, Task<bool>> recover)
    {
        ArgumentNullException.ThrowIfNull(recover);
        return new(QuerySyncRecoveryKind.Custom, recover);
    }
}

/// <summary>What a <see cref="QuerySyncRecovery"/> strategy can see and do while it runs.</summary>
public sealed class QuerySyncRecoveryContext
{
    private readonly QueryEventSynchronizer _owner;
    private int _completed;

    internal QuerySyncRecoveryContext(QueryEventSynchronizer owner, QuerySyncRecoveryReason reason, IReadOnlyList<QueryKey> coveredKeys)
    {
        _owner = owner;
        Reason = reason;
        CoveredKeys = coveredKeys;
    }

    /// <summary>Gets why the recovery runs.</summary>
    public QuerySyncRecoveryReason Reason { get; }

    /// <summary>Gets the query client.</summary>
    public IQueryClient Client => _owner.Client;

    /// <summary>Gets the keys (prefixes) the mappings cover - what the fallback invalidation would invalidate.</summary>
    public IReadOnlyList<QueryKey> CoveredKeys { get; }

    /// <summary>Gets whether <see cref="SetCheckpoint"/> was called.</summary>
    internal bool CheckpointSet { get; private set; }

    /// <summary>
    /// Gets the last applied sequence of <paramref name="stream"/> (<see langword="null"/> for the default stream), or
    /// <see langword="null"/> if no sequenced event has been applied yet - replay from the beginning of what the server keeps.
    /// </summary>
    public long? GetCheckpoint(string? stream = null) => _owner.GetCheckpoint(stream);

    /// <summary>
    /// Records that the cache now reflects every event of <paramref name="stream"/> up to <paramref name="sequence"/>
    /// (for example a snapshot's watermark); older events are discarded as stale.
    /// </summary>
    public void SetCheckpoint(long sequence, string? stream = null)
    {
        ThrowIfCompleted();
        _owner.SetCheckpointCore(stream, sequence);
        CheckpointSet = true;
    }

    /// <summary>
    /// Processes a replayed event through the normal pipeline (metadata, deduplication, ordering, mappings) before any
    /// event received live after the recovery started. Only valid while the recovery runs.
    /// </summary>
    public ValueTask<QueryEventOutcome> ApplyAsync<TEvent>(string eventName, TEvent payload, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventName);
        ThrowIfCompleted();
        return _owner.ProcessInlineAsync(eventName, payload, replay: true, cancellationToken);
    }

    internal void Complete() => Interlocked.Exchange(ref _completed, 1);

    private void ThrowIfCompleted()
    {
        if (Volatile.Read(ref _completed) != 0)
        {
            throw new InvalidOperationException("The recovery has completed; its context can no longer be used.");
        }
    }
}
