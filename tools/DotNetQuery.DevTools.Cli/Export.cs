namespace DotNetQuery.DevTools.Cli;

// Mirror of the shape produced by QueryDevTools.ExportJson() — the out-of-process contract this tool
// consumes. Deserialized with JsonSerializerDefaults.Web (camelCase, case-insensitive).

internal sealed record DevToolsExport(
    StatsDto Stats,
    QueryDto[] Queries,
    EventDto[] Events);

internal sealed record StatsDto(
    int TotalQueries,
    int ActiveQueries,
    int StaleQueries,
    Dictionary<string, int> StatusCounts,
    long FetchesStarted,
    long FetchesSucceeded,
    long FetchesFailed,
    long MutationsStarted,
    long MutationsCompleted,
    double AverageQueryDurationMs);

internal sealed record QueryDto(
    string Key,
    string Status,
    string DataTypeName,
    bool HasData,
    bool IsStale,
    int ObserverCount,
    int FetchCount,
    int FailureCount,
    string? ErrorMessage,
    DateTimeOffset? DataUpdatedAt,
    DateTimeOffset? LastActivityAt,
    double CacheTimeSeconds);

internal sealed record EventDto(
    string Type,
    DateTimeOffset Timestamp,
    string? Key,
    double? DurationMs,
    bool? Succeeded,
    string? Error);
