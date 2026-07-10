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

    /// <summary>Distribution of entries by lifecycle status.</summary>
    public required IReadOnlyDictionary<QueryStatus, int> StatusCounts { get; init; }

    /// <summary>Total query fetches started since monitoring began.</summary>
    public required long FetchesStarted { get; init; }

    /// <summary>Total query fetches that succeeded since monitoring began.</summary>
    public required long FetchesSucceeded { get; init; }

    /// <summary>Total query fetches that failed since monitoring began.</summary>
    public required long FetchesFailed { get; init; }

    /// <summary>Total mutations started since monitoring began.</summary>
    public required long MutationsStarted { get; init; }

    /// <summary>Total mutations completed since monitoring began.</summary>
    public required long MutationsCompleted { get; init; }

    /// <summary>Mean duration of completed query fetches, in milliseconds.</summary>
    public required double AverageQueryDurationMs { get; init; }
}
