namespace DotNetQuery;

/// <summary>Describes the lifecycle status of a query's data.</summary>
public enum QueryStatus
{
    /// <summary>The query has never run and has no data.</summary>
    Idle,

    /// <summary>The query is fetching for the first time and has no data yet.</summary>
    Loading,

    /// <summary>The query has data from a successful fetch.</summary>
    Success,

    /// <summary>The most recent fetch failed.</summary>
    Error,

    /// <summary>The query already has data and is fetching fresh data in the background.</summary>
    Refetching,

    /// <summary>The query is paused (for example, awaiting network reconnection).</summary>
    Paused,
}
