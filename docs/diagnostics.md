# Diagnostics

DotNetQuery emits diagnostics three ways: an in-process **event stream**, OpenTelemetry-native **traces and
metrics**, and a **DevTools** model (with a Blazor panel and a CLI) for inspection.

## Event stream

Subscribe to `IQueryClient.DiagnosticEvent`:

```csharp
client.DiagnosticEvent += e =>
{
    switch (e)
    {
        case QueryStartedEvent s:      logger.LogDebug("start {Key}", s.Key); break;
        case QueryCompletedEvent c:    logger.LogDebug("done {Key} in {Ms}ms", c.Key, c.Duration.TotalMilliseconds); break;
        case QueryFailedEvent f:       logger.LogWarning(f.Error, "failed {Key} after {N} attempts", f.Key, f.FailureCount); break;
        case QueryCancelledEvent x:    logger.LogDebug("cancelled {Key}", x.Key); break;
        case QueryInvalidatedEvent i:  logger.LogDebug("invalidated {Count}", i.Keys.Count); break;
        case QueryObserverErrorEvent o: logger.LogError(o.Error, "a subscriber of {Key} threw", o.Key); break;
        case MutationStartedEvent m:   logger.LogDebug("mutation #{Id} {Key}", m.MutationId, m.MutationKey); break;
        case MutationCompletedEvent m: logger.LogDebug("mutation #{Id} ok={Ok}", m.MutationId, m.Succeeded); break;
    }
};
```

- Query events are **deduplication-accurate**: two callers of the same in-flight key produce one
  `QueryStartedEvent`. Every start ends in exactly one completed, failed, or cancelled event.
- `QueryObserverErrorEvent` reports a `StateChanged` handler that threw. Handler exceptions are isolated:
  they never fail or retry the fetch, and other handlers still run.
- A throwing `DiagnosticEvent` handler is ignored - diagnostics can never break the operation they observe.

## OpenTelemetry

Instrumentation uses BCL `ActivitySource` and `Meter` - no dependency on the OpenTelemetry SDK:

```csharp
tracerProviderBuilder.AddSource(DotNetQueryDiagnostics.ActivitySourceName);   // "DotNetQuery"
meterProviderBuilder.AddMeter(DotNetQueryDiagnostics.MeterName);              // "DotNetQuery"
```

- **Traces** - `dotnetquery.query.fetch` per fetch (tagged with the key; HTTP spans made by the fetcher
  nest under it) and `dotnetquery.mutation` per mutation (tagged with id and key).
- **Metrics** - `dotnetquery.queries.started` / `.succeeded` / `.failed` / `.cancelled`,
  `dotnetquery.query.duration`, `dotnetquery.mutations.started` / `.completed` / `.failed`, and
  `dotnetquery.mutation.duration`.

## DevTools

`QueryDevTools` (namespace `DotNetQuery.DevTools`) is a framework-agnostic inspection model:

```csharp
using var devtools = new QueryDevTools(client);

foreach (var q in devtools.InspectQueries())
    Console.WriteLine($"{q.Key}  {q.Status}  observers={q.ObserverCount}  stale={q.IsStale}");

string? json = devtools.PreviewData(key);            // the cached data, as indented JSON
foreach (var m in devtools.RecentMutations())        // mutation history, newest first
    Console.WriteLine($"#{m.MutationId} {m.MutationKey} {m.Status} {m.Duration}");

var stats = devtools.GetStats();                      // statuses, in-flight, tallies, avg duration
await devtools.RefetchAsync(key);                     // manual actions: refetch, invalidate, cancel, remove
string export = devtools.ExportJson();                // export the full diagnostic state
```

`Changed` fires for every recorded event and every cache change, so UIs can refresh live.

For Blazor, add the visual panel that ships in `DotNetQuery.Blazor` (`@using DotNetQuery.DevTools.Blazor`;
see [Blazor](blazor.md#devtools)):

```razor
<QueryDevToolsPanel Floating="true" />
```

For any app, `tools/DotNetQuery.DevTools.Cli` renders an `ExportJson()` dump (file or stdin) as a console
dashboard.
