# Diagnostics

DotNetQuery emits diagnostics three ways: an in-process **event stream**, OpenTelemetry-native **traces and metrics**, and a **DevTools** model for inspection.

## Event stream

Subscribe to `IQueryClient.DiagnosticEvent` for query/mutation lifecycle events.

```csharp
client.DiagnosticEvent += e =>
{
    switch (e)
    {
        case QueryStartedEvent s:   logger.LogDebug("start {Key}", s.Key); break;
        case QueryCompletedEvent c: logger.LogDebug("done {Key} in {Ms}ms", c.Key, c.Duration.TotalMilliseconds); break;
        case QueryFailedEvent f:    logger.LogWarning(f.Error, "failed {Key}", f.Key); break;
        case QueryInvalidatedEvent i: logger.LogDebug("invalidated {Count}", i.Keys.Count); break;
        case MutationCompletedEvent m: logger.LogDebug("mutation ok={Ok}", m.Succeeded); break;
    }
};
```

Query events are **deduplication-accurate**: two callers of the same in-flight key produce one `QueryStartedEvent`.

## OpenTelemetry

Instrumentation uses BCL `ActivitySource` and `Meter` — no dependency on the OpenTelemetry SDK. Register them by name:

```csharp
tracerProviderBuilder.AddSource(DotNetQueryDiagnostics.ActivitySourceName);   // "DotNetQuery"
meterProviderBuilder.AddMeter(DotNetQueryDiagnostics.MeterName);              // "DotNetQuery"
```

You get:

- **Traces** — one span (`dotnetquery.query.fetch`) per fetch, tagged with the key, with OK/Error status.
- **Metrics** — `dotnetquery.queries.started` / `.succeeded` / `.failed`, `dotnetquery.query.duration` (histogram), and mutation counters.

## DevTools

The `DotNetQuery.DevTools` package gives a framework-agnostic inspection model:

```csharp
using var devtools = new QueryDevTools(client);

foreach (var q in devtools.InspectQueries())
    Console.WriteLine($"{q.Key}  {q.Status}  observers={q.ObserverCount}  stale={q.IsStale}  fetches={q.FetchCount}");

var stats = devtools.GetStats();          // status distribution, tallies, average duration
await devtools.RefetchAsync(key);         // manual actions
string json = devtools.ExportJson();      // export the full diagnostic state
```

For Blazor, drop in the visual panel from `DotNetQuery.DevTools.Blazor`:

```razor
<QueryDevToolsPanel />
```
