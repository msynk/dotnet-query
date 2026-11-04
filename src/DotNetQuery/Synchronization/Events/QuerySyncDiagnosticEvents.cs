namespace DotNetQuery.Synchronization;

/// <summary>
/// Raised on <see cref="IQueryClient.DiagnosticEvent"/> for every event a <see cref="QueryEventSynchronizer"/> processed
/// (or dropped). Payloads are never included.
/// </summary>
public sealed record QuerySyncEventProcessedEvent : DotNetQueryEvent
{
    /// <summary>The synchronizer's <see cref="QueryEventSyncOptions.Name"/>.</summary>
    public required string Synchronizer { get; init; }

    /// <summary>The event name.</summary>
    public required string EventName { get; init; }

    /// <summary>What happened to the event.</summary>
    public required QueryEventOutcome Outcome { get; init; }

    /// <summary>The event id, if the event has one.</summary>
    public string? EventId { get; init; }

    /// <summary>The event's stream sequence, if any.</summary>
    public long? Sequence { get; init; }

    /// <summary>Whether a recovery replayed the event.</summary>
    public bool IsReplay { get; init; }

    /// <summary>How long processing took (excluding the refetches it caused).</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>The error of a failed mapping or a rejected event.</summary>
    public Exception? Error { get; init; }
}

/// <summary>Raised on <see cref="IQueryClient.DiagnosticEvent"/> when a synchronizer's <see cref="QueryEventSynchronizer.Status"/> changes.</summary>
public sealed record QuerySyncStatusChangedEvent : DotNetQueryEvent
{
    /// <summary>The synchronizer's <see cref="QueryEventSyncOptions.Name"/>.</summary>
    public required string Synchronizer { get; init; }

    /// <summary>The new status.</summary>
    public required QuerySyncStatus Status { get; init; }

    /// <summary>The previous status.</summary>
    public required QuerySyncStatus Previous { get; init; }

    /// <summary>The error behind the transition (a lost connection, a failed recovery), if any.</summary>
    public Exception? Error { get; init; }
}

/// <summary>Raised on <see cref="IQueryClient.DiagnosticEvent"/> when a synchronizer's recovery completes.</summary>
public sealed record QuerySyncRecoveryCompletedEvent : DotNetQueryEvent
{
    /// <summary>The synchronizer's <see cref="QueryEventSyncOptions.Name"/>.</summary>
    public required string Synchronizer { get; init; }

    /// <summary>Why the recovery ran.</summary>
    public required QuerySyncRecoveryReason Reason { get; init; }

    /// <summary>The configured strategy.</summary>
    public required QuerySyncRecoveryKind Strategy { get; init; }

    /// <summary>Whether the strategy failed or reported missing history, so the cache was reconciled by invalidation instead.</summary>
    public bool FellBack { get; init; }

    /// <summary>Whether the cache was reconciled (by the strategy or its fallback).</summary>
    public required bool Succeeded { get; init; }

    /// <summary>How long the recovery took, including the refetches of observed queries it caused.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>The strategy's error, if any.</summary>
    public Exception? Error { get; init; }
}
