namespace DotNetQuery;

/// <summary>Base type for all DotNetQuery diagnostic events emitted via <see cref="IQueryClient.DiagnosticEvent"/>.</summary>
public abstract record DotNetQueryEvent
{
    /// <summary>Gets the UTC time at which the event occurred.</summary>
    public required DateTimeOffset Timestamp { get; init; }
}

/// <summary>Raised when a query begins a real (non-deduplicated) fetch.</summary>
public sealed record QueryStartedEvent : DotNetQueryEvent
{
    /// <summary>The key of the query that started fetching.</summary>
    public required QueryKey Key { get; init; }
}

/// <summary>Raised when a query's fetch completes successfully.</summary>
public sealed record QueryCompletedEvent : DotNetQueryEvent
{
    /// <summary>The key of the completed query.</summary>
    public required QueryKey Key { get; init; }

    /// <summary>How long the fetch took (including retries).</summary>
    public required TimeSpan Duration { get; init; }
}

/// <summary>Raised when a query's fetch ultimately fails after exhausting retries.</summary>
public sealed record QueryFailedEvent : DotNetQueryEvent
{
    /// <summary>The key of the failed query.</summary>
    public required QueryKey Key { get; init; }

    /// <summary>The terminal error.</summary>
    public required Exception Error { get; init; }

    /// <summary>The number of failed attempts.</summary>
    public required int FailureCount { get; init; }

    /// <summary>How long the fetch took before failing.</summary>
    public required TimeSpan Duration { get; init; }
}

/// <summary>Raised when queries are invalidated.</summary>
public sealed record QueryInvalidatedEvent : DotNetQueryEvent
{
    /// <summary>The keys that were invalidated.</summary>
    public required IReadOnlyList<QueryKey> Keys { get; init; }
}

/// <summary>Raised when a mutation begins.</summary>
public sealed record MutationStartedEvent : DotNetQueryEvent;

/// <summary>Raised when a mutation completes (successfully or not).</summary>
public sealed record MutationCompletedEvent : DotNetQueryEvent
{
    /// <summary>Whether the mutation succeeded.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>The error, when the mutation failed.</summary>
    public Exception? Error { get; init; }

    /// <summary>How long the mutation took.</summary>
    public required TimeSpan Duration { get; init; }
}
