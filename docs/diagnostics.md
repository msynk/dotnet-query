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
        case QueryRetryEvent r:        logger.LogDebug(r.Error, "retrying {Key} (failure {N})", r.Key, r.FailureCount); break;
        case OptimisticUpdateAppliedEvent a:    logger.LogDebug("mutation #{Id} wrote {Keys} optimistically", a.MutationId, a.Keys); break;
        case OptimisticUpdateRolledBackEvent b: logger.LogDebug("mutation #{Id} rolled back {Keys}", b.MutationId, b.Restored); break;
    }
};
```

## Logging

`AddDiagnosticLogging()` writes every client's event stream to the registered `ILoggerFactory` (category
`DotNetQuery`), with source-generated `LoggerMessage`s (allocation-light, trim/AOT-safe):

```csharp
builder.Services.AddDotNetQuery(o => o.DiagnosticKeyFormatter = key => $"{key[0]}/...")
    .AddDiagnosticLogging(new QueryDiagnosticsLoggingOptions { RoutineLevel = LogLevel.Trace });

// Without DI:
using var subscription = client.LogDiagnostics(loggerFactory.CreateLogger("DotNetQuery"));
```

| Event | Level | Event id |
| --- | --- | --- |
| Fetch started / completed / cancelled, invalidation, mutation started / succeeded, optimistic write / rollback | `RoutineLevel` (Debug) | 1000, 1001, 1003, 1005, 1100, 1101, 1103, 1104 |
| Retry | `RetryLevel` (Information) | 1004 |
| Fetch or mutation failed (with the exception) | `FailureLevel` (Warning) | 1002, 1102 |
| A `StateChanged` subscriber threw | Error | 1006 |

Keys are formatted with `QueryDiagnosticsLoggingOptions.KeyFormatter`, else the client's `DiagnosticKeyFormatter`.
Mutation variables and cached data are never logged.

## Query and mutation meta

`QueryOptions.Meta` and `MutationOptions.Meta` attach application data that DotNetQuery never reads - TanStack Query's
`meta` - and hand it to cache-wide handlers: `QueryCompletedEvent.Meta`, `QueryFailedEvent.Meta`,
`MutationStartedEvent.Meta`, `MutationCompletedEvent.Meta`, `QueryDescriptor.Meta` and `MutationDescriptor.Meta`.
Typical use: one global error toast whose message each query or mutation chooses.

```csharp
builder.Services.AddDotNetQuery(o => o
    .SetQueryDefaults(QueryKey.Create("todos"), new QueryOptions { Meta = new Dictionary<string, object?> { ["error"] = "Could not load your todos" } })
    .SetMutationDefaults(new MutationDefaults { Meta = new Dictionary<string, object?> { ["error"] = "Saving failed" } }));

client.DiagnosticEvent += e =>
{
    switch (e)
    {
        case QueryFailedEvent { Meta: { } meta } when meta.TryGetValue("error", out var message): toasts.Show((string)message!); break;
        case MutationCompletedEvent { Succeeded: false, Meta: { } meta } when meta.TryGetValue("error", out var message): toasts.Show((string)message!); break;
    }
};
```

Layering follows the other options: the most specific `Meta` that is set wins (dictionaries are not merged).

### Keeping secrets out of diagnostics

```csharp
builder.Services.AddDotNetQuery(o =>
{
    o.IncludeVariablesInDiagnostics = false;               // MutationStartedEvent.Variables (and DevTools) stay empty
    o.DiagnosticKeyFormatter = key => $"{key[0]}/...";     // OpenTelemetry key tags without personal data
});
```

Events carry query keys, error objects and (unless disabled) mutation variables; cached data never appears in
events. The DevTools panel's `ShowData="false"` hides data previews in the UI.

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

For any app, `src/DotNetQuery.DevTools.Cli` renders an `ExportJson()` dump (file or stdin) as a console
dashboard.

### Live DevTools for any app

Desktop, mobile, console and server apps have no Blazor panel; serve the DevTools model instead and watch it live from
the CLI (development only):

```csharp
#if DEBUG
var devtools = new QueryDevTools(client);
await using var server = QueryDevToolsServer.Start(devtools, new QueryDevToolsServerOptions { Port = 9230 });
#endif
```

```bash
dotnet run --project src/DotNetQuery.DevTools.Cli -- watch http://127.0.0.1:9230/            # refreshes every second
dotnet run --project src/DotNetQuery.DevTools.Cli -- http://127.0.0.1:9230/                  # one snapshot
```

The server listens on 127.0.0.1 only and serves the same export as `ExportJson()` (keys, statuses, timings, errors -
never cached data or mutation variables). On a shared machine set `AccessToken` and pass it as `?token=...`. It uses
reflection-based JSON (like `ExportJson`) and is not available in the browser.

### Debugging with DevTools

The `QueryDevTools` model (and the Blazor panel on top of it) answers "why did this query fetch, refetch, fail
or go stale?":

| Feature | Model API | Panel |
| --- | --- | --- |
| Per-query lifecycle timeline (starts, retries, completions, failures, cancellations, invalidations, optimistic writes) | `Timeline(key)` | details pane |
| Per-query profile (fetches, failures, retries, cancellations, average/max duration) | `ProfileQueries()` | details pane, CLI "Slowest queries" |
| Key hierarchy | `KeyTree()` | "Tree" toggle |
| Status filter and key search | `Cache.FindAll(filter)` | filter box, status select |
| Optimistic writes and rollbacks per mutation | `RecentMutations()` (`OptimisticKeys`, `RolledBackKeys`, `RefetchedKeys`) | Mutations tab |
| Snapshots and time travel | `TakeSnapshot`, `Snapshots`, `RestoreSnapshot` | Snapshots tab |
| Controlled state editing | `ResetAsync`, `RefetchAsync`, `InvalidateAsync`, `CancelAsync`, `RemoveQuery` | row and details actions |
| Durable offline queue inspection (pending, dead letters, retry, discard) | `AttachQueue(queue)`, `QueuedMutations`, `DeadLetters` | Offline queue tab (`Queue` parameter) |
| Diagnostic report | `ExportJson()` (stats, queries, mutations, profiles, events) | CLI renders it |

The event log and profiles are bounded by `eventLogCapacity`; the snapshot list keeps the last 20.
