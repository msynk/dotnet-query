namespace DotNetQuery.DevTools;

/// <summary>Aggregate statistics about the query cache and observed activity.</summary>
public sealed record QueryDevToolsStats
{
    /// <summary>Total number of entries in the cache.</summary>
    public required int TotalQueries { get; init; }

    /// <summary>Number of entries with at least one observer.</summary>
    public required int ActiveQueries { get; init; }

    /// <summary>Number of entries whose data is currently stale.</summary>
    public required int StaleQueries { get; init; }

    /// <summary>Number of entries with a fetch currently in flight.</summary>
    public int FetchingQueries { get; init; }

    /// <summary>Distribution of entries by lifecycle status.</summary>
    public required IReadOnlyDictionary<QueryStatus, int> StatusCounts { get; init; }

    /// <summary>Total query fetches started since monitoring began.</summary>
    public required long FetchesStarted { get; init; }

    /// <summary>Total query fetches that succeeded since monitoring began.</summary>
    public required long FetchesSucceeded { get; init; }

    /// <summary>Total query fetches that failed since monitoring began.</summary>
    public required long FetchesFailed { get; init; }

    /// <summary>Total query fetches cancelled (superseded, cancelled, or paused) since monitoring began.</summary>
    public long FetchesCancelled { get; init; }

    /// <summary>Total mutations started since monitoring began.</summary>
    public required long MutationsStarted { get; init; }

    /// <summary>Total mutations completed since monitoring began.</summary>
    public required long MutationsCompleted { get; init; }

    /// <summary>Total mutations that failed since monitoring began.</summary>
    public long MutationsFailed { get; init; }

    /// <summary>Number of mutations in the history that are still running.</summary>
    public int PendingMutations { get; init; }

    /// <summary>Mean duration of completed query fetches, in milliseconds.</summary>
    public required double AverageQueryDurationMs { get; init; }
}

/// <summary>One mutation run in the DevTools mutation history.</summary>
public sealed record MutationRecord
{
    /// <summary>The client-unique id of the run.</summary>
    public required long MutationId { get; init; }

    /// <summary>The optional key identifying the kind of mutation.</summary>
    public QueryKey? MutationKey { get; init; }

    /// <summary>The variables the mutation ran with (a live reference; treat as read-only).</summary>
    public object? Variables { get; init; }

    /// <summary>The mutation's current status.</summary>
    public required MutationStatus Status { get; init; }

    /// <summary>When the mutation was submitted.</summary>
    public required DateTimeOffset SubmittedAt { get; init; }

    /// <summary>How long the mutation took, once settled.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>The number of failed attempts.</summary>
    public int FailureCount { get; init; }

    /// <summary>The error message, when the mutation failed.</summary>
    public string? ErrorMessage { get; init; }
}
