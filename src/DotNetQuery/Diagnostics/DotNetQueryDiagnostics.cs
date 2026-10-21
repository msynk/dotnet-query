using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace DotNetQuery;

/// <summary>
/// OpenTelemetry-native instrumentation for DotNetQuery. Exposes an <see cref="System.Diagnostics.ActivitySource"/>
/// for distributed tracing and a <see cref="System.Diagnostics.Metrics.Meter"/> for metrics.
/// </summary>
/// <remarks>
/// To collect these with OpenTelemetry, register the source and meter by name:
/// <code>
/// tracerProviderBuilder.AddSource(DotNetQueryDiagnostics.ActivitySourceName);
/// meterProviderBuilder.AddMeter(DotNetQueryDiagnostics.MeterName);
/// </code>
/// No dependency on the OpenTelemetry SDK is required - these are BCL types.
/// </remarks>
public static class DotNetQueryDiagnostics
{
    /// <summary>The name of the <see cref="System.Diagnostics.ActivitySource"/> for query/mutation traces.</summary>
    public const string ActivitySourceName = "DotNetQuery";

    /// <summary>The name of the <see cref="System.Diagnostics.Metrics.Meter"/> for query/mutation metrics.</summary>
    public const string MeterName = "DotNetQuery";

    private static readonly string Version =
        typeof(DotNetQueryDiagnostics).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName, Version);

    private static readonly Meter Meter = new(MeterName, Version);

    internal static readonly Counter<long> QueriesStarted =
        Meter.CreateCounter<long>("dotnetquery.queries.started", unit: "{query}", description: "Number of query fetches started.");

    internal static readonly Counter<long> QueriesSucceeded =
        Meter.CreateCounter<long>("dotnetquery.queries.succeeded", unit: "{query}", description: "Number of query fetches that succeeded.");

    internal static readonly Counter<long> QueriesFailed =
        Meter.CreateCounter<long>("dotnetquery.queries.failed", unit: "{query}", description: "Number of query fetches that failed.");

    internal static readonly Counter<long> QueriesCancelled =
        Meter.CreateCounter<long>("dotnetquery.queries.cancelled", unit: "{query}", description: "Number of query fetches cancelled before settling.");

    internal static readonly Histogram<double> QueryDuration =
        Meter.CreateHistogram<double>("dotnetquery.query.duration", unit: "ms", description: "Duration of query fetches.");

    internal static readonly Counter<long> MutationsStarted =
        Meter.CreateCounter<long>("dotnetquery.mutations.started", unit: "{mutation}", description: "Number of mutations started.");

    internal static readonly Counter<long> MutationsCompleted =
        Meter.CreateCounter<long>("dotnetquery.mutations.completed", unit: "{mutation}", description: "Number of mutations completed.");

    internal static readonly Counter<long> MutationsFailed =
        Meter.CreateCounter<long>("dotnetquery.mutations.failed", unit: "{mutation}", description: "Number of mutations that failed.");

    internal static readonly Histogram<double> MutationDuration =
        Meter.CreateHistogram<double>("dotnetquery.mutation.duration", unit: "ms", description: "Duration of mutations.");
}

/// <summary>
/// Internal sink through which cache entries and mutations report lifecycle events back to the
/// owning <see cref="QueryClient"/>, which fans them out to public events, traces and metrics.
/// Implementations must not throw.
/// </summary>
internal interface IQueryEventSink
{
    /// <summary>Reports a fetch start and returns an opaque token (the trace span) for the matching end call.</summary>
    object? QueryFetchStarted(QueryKey key);

    void QueryFetchSucceeded(QueryKey key, TimeSpan duration, object? token);

    void QueryFetchFailed(QueryKey key, Exception error, int failureCount, TimeSpan duration, object? token);

    void QueryFetchCancelled(QueryKey key, TimeSpan duration, object? token);

    void ObserverFailed(QueryKey key, Exception error);

    /// <summary>Reports a mutation start and returns its id and an opaque token for the matching end call.</summary>
    (long Id, object? Token) MutationStarted(QueryKey? mutationKey, object? variables);

    void MutationCompleted(long id, QueryKey? mutationKey, bool succeeded, Exception? error, int failureCount, TimeSpan duration, object? token);
}
