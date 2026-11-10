# Real-time cache synchronization

Keep the query cache in step with server-side changes as they happen: a server publishes events (over SignalR, a
message broker, server-sent events - any transport), and a `QueryEventSynchronizer` turns each one into cache
operations - invalidations, typed incremental updates, entity upserts and removals - with duplicate protection,
ordering by version or sequence, coalesced refetches, and recovery after a reconnect.

- The engine lives in the core package (`Msynk.DotNetQuery`, namespace `DotNetQuery.Synchronization`) and has no
  transport, browser or UI dependency.
- `Msynk.DotNetQuery.SignalR` adds `HubConnectionQueryEventSource`, the SignalR transport, and `AddSignalRSync` for DI.
- `Msynk.DotNetQuery.Blazor` adds component-scoped mappings (`UseQueryEvents`), a status hook (`UseQuerySyncStatus`),
  a cross-tab relay over `BroadcastChannel` (`AddCrossTabEventRelay`) and a "Sync" tab in the DevTools panel.

The runnable demo [`demo/DotNetQuery.Demo.Realtime`](../demo/DotNetQuery.Demo.Realtime) shows everything on
this page end to end; the TodoApp demo's WebAssembly pages use it in a browser.

## Installation and registration

```bash
dotnet add package Msynk.DotNetQuery.SignalR
```

```csharp
builder.Services.AddDotNetQuery()   // or AddDotNetQueryBlazor() - the synchronizer gets the client's lifetime
    .AddSignalRSync(
        _ => new HubConnectionBuilder()
            .WithUrl("https://api.example.com/hubs/todos")
            .WithAutomaticReconnect()
            .Build(),
        sync => sync.Map<QueryEventEnvelope<Todo>>("TodoChanged", m => m
            .Upsert(TodoQueries.Entity, e => e.Payload)
            .Invalidate(TodoQueries.Stats)),
        new HubConnectionQueryEventSourceOptions { OwnsConnection = true });

// Once, at startup (or from a component in Blazor WebAssembly, see below):
await app.Services.GetRequiredService<QueryEventSynchronizer>().StartAsync();
```

Without DI, compose the pieces yourself:

```csharp
var source = new HubConnectionQueryEventSource(connection);           // the connection stays the app's
await using var sync = new QueryEventSynchronizer(client, source);
sync.Map<Todo>("TodoChanged", m => m.Upsert(TodoQueries.Entity, t => t));
await sync.StartAsync();
```

**Lifetimes.** Give the synchronizer the lifetime of the query client it updates: a singleton with a singleton client
(desktop, mobile, console, WebAssembly), scoped with a scoped client. Never create a synchronizer or a connection per
component - components register *mappings* on the shared synchronizer (see [Blazor](#blazor)).

**Ownership.** The synchronizer never disposes the query client. It disposes its source only when constructed with
`ownsSource: true`, and the SignalR source disposes the `HubConnection` only with `OwnsConnection = true`; a connection
shared with other features of the app is left running.

## Mappings

`sync.Map<TEvent>(eventName, configure)` describes what one event does to the cache. Several mappings may handle the
same event (each applied independently); dispose the returned registration to remove one. Hub methods must send exactly
one argument - the payload, deserialized as `TEvent`.

| Operation | Use it when | What it does |
| --- | --- | --- |
| `Invalidate(key)` / `Invalidate(e => key)` / `Invalidate(e => filter)` | The event says *something changed*, or you cannot apply it safely | Marks matching queries stale; observed ones refetch. Coalesced per burst. Always correct. |
| `SetData(e => key, e => data, createIfMissing)` | The event carries the complete value of one query | Replaces that query's data - only if it is cached, unless `createIfMissing`. |
| `Update<TData>(key, (data, e) => newData)` | Custom shapes: counters, aggregates, projections | Transforms every query of type `TData` under the prefix that holds data. Return the current value unchanged to skip a query. |
| `Upsert(entity, e => item, whenMissing)` | The event carries a changed entity | Replaces it in detail queries and wherever lists and pages hold it; lists without it follow `whenMissing`. |
| `Remove(entity, e => id, detail)` | The event says an entity was deleted | Removes it from lists and pages; invalidates (or removes) its detail queries. |
| `Handle((context, ct) => ...)` | Anything else | Your code, with `context.Event`, `context.Metadata`, `context.Client` and coalesced `context.Invalidate(...)`. |

Filter with `Where(e => ...)`. A mapping's data operations commit **atomically** (observers see all or none of them),
never create cache entries unless asked to, and never touch queries without data. Invalidations and handlers run after
them, in the declared order.

### Entities

Describe an entity once and use it in every mapping:

```csharp
public static readonly QueryEntity<Todo, int> Entity = QueryEntity.Create<Todo, int>(
    TodoQueries.All,              // every query holding todos lives under this prefix
    t => t.Id,                    // identity
    version: t => t.Version,      // optional: an older item never replaces a newer one
    detailKey: TodoQueries.ById); // optional: find details by key (also covers a first fetch in flight)
```

Recognized without configuration: detail queries of `TItem` and lists of `List<TItem>`, `IReadOnlyList<TItem>`,
`TItem[]`, `ImmutableArray<TItem>` and `ImmutableList<TItem>`. Add your own shapes:

```csharp
var entity = Entity
    .WithCollection<Paged<Todo>>(p => p.Items, (p, items) => p with { Items = items }, refetchOnMembershipChange: true)
    .WithInfinitePages<string?, TodoPage>(page => page.Items, (page, items) => page with { Items = items },
        refetchOnMembershipChange: false); // cursor pagination: local inserts/removals keep the cursors valid
```

`whenMissing` decides what an upsert does to a list that does not contain the entity: `Invalidate` (the default - its
membership, sort order or totals cannot be known from one event, so it refetches), `Append`, `Prepend` (into the first
or last page of an infinite query) or `Ignore`. Collections marked `refetchOnMembershipChange` (offset pagination,
totals) are refetched after any insertion or removal; replacing an item in place never refetches.

### Invalidation or incremental updates?

Invalidation is always correct and costs a refetch of what is on screen. Incremental updates avoid the refetch, but are
only as correct as the event: prefer them when events carry complete entities **and** a version, and keep invalidation
for filtered, sorted or paginated views whose membership an event cannot decide, for aggregates, and for events without
reliable ordering. Mixing is normal: upsert the entity, invalidate the stats.

## Event metadata, envelopes and ordering

The synchronizer reads each event's `QueryEventMetadata`: `EventId`, `EntityId` + `Version`, `Sequence` (+ `Stream`),
`Scope`, `CorrelationId` and a diagnostic `Timestamp`. Supply what your contract guarantees:

```csharp
// Your own contract:
sync.Map<TodoChanged>("TodoChanged", m => m
    .WithMetadata(e => new QueryEventMetadata { EventId = e.MessageId, EntityId = e.Todo.Id.ToString(), Version = e.Todo.Version })
    .Upsert(Entity, e => e.Todo));

// Or the ready-made envelope (implements IQueryEventMetadataProvider, so no extractor is needed):
await hub.Clients.Group(tenant).SendAsync("TodoChanged", new QueryEventEnvelope<Todo>
{
    EventId = Guid.NewGuid().ToString("N"), EntityId = todo.Id.ToString(), Version = todo.Version,
    Sequence = nextSequence, Scope = tenant, Payload = todo,
});
```

| Metadata | Guarantee it enables |
| --- | --- |
| `EventId` | A redelivered event is discarded within the deduplication window (`DeduplicationWindow`, default 10 minutes, bounded by `DeduplicationCapacity`). Set `RequireEventId` to reject events without one. |
| `EntityId` + `Version` | An event whose version is not greater than the last applied version of its entity is discarded as stale (tracking bounded by `VersionTrackingCapacity`). |
| `Version` on the entity (`QueryEntity.Create(version:)`) | A cached item is replaced only by a newer one - whoever wrote the cached one (an event, a fetch, a mutation response). |
| `Sequence` | Events at or below the stream's checkpoint are discarded. With `ContiguousSequences`, a jump is a **gap**: the event is held back and a recovery runs. |
| `Scope` | Events of another scope than `synchronizer.Scope` are ignored (events without a scope apply everywhere). `ChangeScope` on tenant switches resets the ordering state. |

Timestamps never decide ordering - clocks of different machines do not establish an authoritative order. Without
versions or sequences, there is no ordering guarantee: use invalidation mappings.

An event id is recorded as processed only **after** its mappings ran; a mapping that throws has its targets invalidated
instead (outcome `Failed`), and other mappings of the event still apply. Deduplication is in memory: it does not provide
exactly-once delivery across process restarts.

## Processing model

Events are queued (bounded, `QueueCapacity`, default 10,000) and processed one at a time, in arrival order, on a
background loop - the transport's receive callback only enqueues, so slow cache work never stalls it. The pipeline:

1. read metadata (malformed: `Rejected`);
2. scope check (`OutOfScope`), duplicate check (`Duplicate`), sequence check (`Stale`, `Gap`), version check (`Stale`);
3. each mapping: `Where`, local-echo check (`Echo`), atomic data writes, then invalidations and handlers;
4. supersede fetches that were already in flight for written queries (cancel and refetch, unless they started after the
   event arrived), so an older response cannot overwrite the event;
5. record the id, sequence and version; report the outcome (diagnostics, metrics);
6. once the queue drains (or every `MaxBatchSize` events), issue the burst's invalidations - a burst refetches each
   query once.

`ProcessAsync(name, payload)` feeds an event directly and returns its `QueryEventOutcome`; `Post` queues without waiting.
When the queue is full, `OverflowPolicy.Resynchronize` (default) drops the event and schedules a recovery - nothing is
silently lost, because the recovery reconciles what was dropped - while `OverflowPolicy.Wait` makes the transport wait
for room (backpressure; with SignalR that delays its keep-alive handling).

## Reconnects and recovery

A recovery reconciles the cache after events may have been missed. It runs after a reconnect, a sequence gap, a queue
overflow, on the first connection (`RecoverOnConnect`, default on - queries fetched before the subscription existed may
be outdated) and on `ResynchronizeAsync()`. It runs after the events queued before it was requested; concurrent requests
coalesce, and a request made while a recovery runs triggers one more afterwards, so the final state is always reconciled.

| Strategy | Requires | Behavior |
| --- | --- | --- |
| `QuerySyncRecovery.Invalidate` (default) | Nothing | Invalidates every covered query; observed ones refetch. |
| `QuerySyncRecovery.Snapshot(fn)` | An endpoint returning authoritative state | Your code writes it to the cache; call `context.SetCheckpoint(watermark)` if the snapshot carries a sequence. |
| `QuerySyncRecovery.Replay(fn)` | A server that **retains** its event history and can resume from a sequence | Reads `context.GetCheckpoint()`, fetches the missed events and feeds them through `context.ApplyAsync` (dedup and ordering still apply). Return `false` when the server cannot replay from that checkpoint. |
| `QuerySyncRecovery.Custom(fn)` | - | Your reconciliation; return `false` to fall back. |

Every strategy falls back to invalidation when it throws, returns `false`, or (with `ContiguousSequences`) leaves a gap
- a recovery never leaves the cache silently outdated. A client cannot recover events the server never retained: replay
only works against a server-side history.

```csharp
Recovery = QuerySyncRecovery.Replay(async (context, ct) =>
{
    if (context.GetCheckpoint() is not { } checkpoint)
    {
        return false; // nothing to resume from (first connect): invalidate instead
    }

    var missed = await connection.InvokeAsync<List<QueryEventEnvelope<Todo>>>("EventsSince", checkpoint, ct);
    foreach (var e in missed)
    {
        await context.ApplyAsync("TodoChanged", e, ct);
    }

    return true;
}),
```

**What a recovery covers.** Every mapping's static keys (`Invalidate(key)`, `Update(key, ...)`, the entity key of
`Upsert`/`Remove`) plus what it declares with `Covers(...)`. A mapping that only targets keys computed from the event must
declare `Covers` (or `NoRecovery()` for handlers with nothing to reconcile); registration fails otherwise, so a recovery
can never miss a query.

### Connection lifecycle (SignalR)

`HubConnectionQueryEventSource` reports the connection's state, which drives `synchronizer.Status`:

`Disconnected` → `Connecting` → (`Resubscribing`) → `Synchronizing` → `Synchronized`; on a lost connection
`Reconnecting` → `Resubscribing` → `Synchronizing` → `Synchronized`; a failed recovery leaves `RecoveryFailed`
(retry with `ResynchronizeAsync`).

- **Handlers** (`connection.On`) survive reconnects; the source registers each event name once.
- **Server-side group memberships do not survive a new connection.** Restore them in `OnConnected`, which runs on start
  and after every reconnect, before the source reports `Connected` - so the recovery sees every event published after
  the rejoin:

  ```csharp
  new HubConnectionQueryEventSourceOptions { OnConnected = (hub, ct) => hub.InvokeAsync("JoinTenant", tenantId, ct) }
  ```

- **Automatic reconnection** (`WithAutomaticReconnect`) covers lost connections. `RestartDelay` covers failed starts, a
  failed `OnConnected`, and connections closed with an error (reconnection gave up): return the delay before the next
  attempt, or `null` to give up - for example for authorization failures, which retrying cannot fix. The source then
  reports `Disconnected` with the error.
- **Access tokens** are renewed by SignalR's `AccessTokenProvider`, which it calls on every (re)connect.

## Mutations and events

The synchronizer uses the existing optimistic-update machinery; it adds nothing to reconcile by hand.

- An event that writes a query while a mutation's optimistic update is pending lands **on top** of it. If the mutation
  fails, its rollback sees the foreign write and refetches the query instead of restoring the old snapshot over the
  event's change.
- An event that invalidates a query with pending optimistic writes defers the refetch until those mutations settle, so
  optimistic values are not replaced by server data that lacks them.
- When the server broadcasts the result of your own mutation before its response arrives, the event's upsert
  (newer version) replaces the optimistic value; with versions on the entity, a late, older response or event cannot
  regress it.
- `IgnoreLocalEchoes()` on a mapping plus `sync.MarkLocalOrigin(correlationId)` before mutating (send the id to the
  server, which echoes it in `CorrelationId`) skips the echo of a change this client already reconciles itself.
  Correlation is per operation - never suppress events just because they come from the same user; their other
  sessions need them.

Client-side reconciliation does not replace server-side concurrency control (version checks on writes) or authorization.

## Blazor

```csharp
// Component-scoped mapping: exists while the component does, follows its parameters.
protected override void OnParametersSet() =>
    UseQueryEvents<Message>(Sync, "MessageAdded", m => m
        .Where(msg => msg.RoomId == RoomId)
        .Update<List<Message>>(ChatKeys.Room(RoomId), (list, msg) => [.. list, msg]),
        dependsOn: RoomId);
```

```razor
@* Re-renders on every status change *@
<span class="live">@UseQuerySyncStatus(Sync)</span>
```

In Blazor WebAssembly, register the synchronizer in the client project and start it from a component (the renderer's
scope holds the app's `IQueryClient`, not the root provider); see the TodoApp demo's `LiveUpdates.razor`. When the
prerendered data was just hydrated, `RecoverOnConnect = false` avoids an immediate refetch.

## Cross-tab relay

`AddCrossTabEventRelay()` (Blazor) or `new QueryEventRelay(synchronizer, channel).Start()` (any `IQuerySyncChannel`)
relays **notices** between tabs: when one tab processes an event or recovers, the others invalidate what *their own*
mappings of that event cover. Place `<DotNetQueryCrossTabEvents />` once in an interactive layout.

- Notices carry no payload, no data and no keys - only the event name and id, the scope and a schema version - so a tab
  cannot make another touch anything its own mappings do not cover.
- Loop prevention: notices received are never relayed again; each carries an origin and a message id (bounded
  deduplication); a tab that processed the same event id itself skips the notice.
- Isolation: notices of another scope or another `ChannelName` (use your app version) are ignored; malformed messages are
  ignored and reported on `Error`. Tabs of one origin share the browser profile's sign-in - combine with
  `ResetOnUserChange` and `ChangeScope` on tenant switches.
- Without `BroadcastChannel` (or while prerendering) nothing is relayed; every tab keeps working on its own.

The relay coordinates freshness; it is not a consistency mechanism - each tab still reconciles with the server. To
replicate cached *data* between tabs, use `QuerySynchronizer` (`AddCrossTabSync`) - but not for queries that every tab
also receives events for: non-idempotent updates (a counter) would then apply twice.

## Diagnostics

- **Diagnostic events** on `IQueryClient.DiagnosticEvent`: `QuerySyncEventProcessedEvent` (event name, outcome, id,
  sequence, replay, duration, error - never the payload), `QuerySyncStatusChangedEvent`, `QuerySyncRecoveryCompletedEvent`.
- **Logging** (`AddDiagnosticLogging`): event ids 1200-1202; failures and rejected events at the failure level, gaps,
  drops, status changes and recoveries at the retry level, everything else at the routine level.
- **Metrics** (meter `DotNetQuery`): `dotnetquery.sync.events.received`, `dotnetquery.sync.events.processed` (tag
  `dotnetquery.sync.outcome`), `dotnetquery.sync.queue.depth`, `dotnetquery.sync.recoveries` (tags `reason`, `result`),
  `dotnetquery.sync.recovery.duration`; every instrument is tagged `dotnetquery.sync.name`.
- **Tracing**: a `dotnetquery.sync.recovery` activity per recovery.
- **`sync.GetStatistics()`**: status, mappings, covered keys, counters, queue depth, checkpoints, last event and
  synchronization times, recent errors (messages only), pending recovery.
- **DevTools**: `devTools.AttachSynchronizer(sync)` adds it to `Synchronizers` and the JSON export (and so to the live
  DevTools server and CLI); the Blazor panel shows a "Sync" tab with `<QueryDevToolsPanel Synchronizers="@(new[] { Sync })" />`.

## Performance

Measured with BenchmarkDotNet (ShortRun, see [the baseline](../tests/DotNetQuery.Benchmarks/Baselines)): one event end
to end (queue, metadata, deduplication, mapping, atomic write) costs ~2.6 µs with 10 cached queries and ~19 µs with
1,000 - data mappings that match by key prefix scan the cache once per event, so the cost grows with the number of cached
queries. A rejected duplicate costs ~1.5 µs; a burst of invalidations costs ~0.3 µs per event, because it is coalesced
into one invalidation.

- Prefer exact keys (`SetData`, a `detailKey` on the entity) where possible; keep `QueryEntity` key prefixes narrow.
- Keep handlers short - later events wait for them.
- Size `QueueCapacity` for your bursts; watch `dotnetquery.sync.queue.depth` and `Dropped`.
- `QueryClientOptions.MaxConcurrentFetches` bounds the refetches recoveries and invalidations cause.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| Events never apply | `GetStatistics().Mappings` lists the event name? The hub method sends exactly one argument of the mapped type? Outcome `NoMapping`/`Filtered`/`OutOfScope` in the diagnostic events? |
| Events stop after a reconnect | Group memberships: rejoin them in `OnConnected`. |
| Everything refetches after each reconnect | That is the default recovery. Use `Replay` against a server history, or narrow `Covers`. |
| `Stale` outcomes for events that should apply | Versions or sequences must increase; after a tenant switch call `ChangeScope`. |
| Recoveries keep running | `ContiguousSequences` with a server whose sequences are not contiguous per stream - turn it off or fix `Stream`. |
| A payload fails to deserialize | SignalR logs and drops it before the synchronizer sees it - enable the connection's logging; in trimmed apps add the payload type to the JSON context passed to `AddJsonProtocol`. |

## Supported environments and limitations

- .NET 8 and .NET 10; trim- and NativeAOT-compatible (configure source-generated JSON for SignalR payloads with
  `AddJsonProtocol(o => o.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, AppJson.Default))`).
- Desktop, mobile, console, ASP.NET Core and Blazor (Server and WebAssembly). The cross-tab relay needs a browser with
  `BroadcastChannel`; elsewhere use any `IQuerySyncChannel` or none.
- In-memory deduplication and ordering state do not survive restarts; persist `GetCheckpoint()` and restore it with
  `SetCheckpoint` if a replay should resume across restarts.
- Incremental updates are only as correct as the events: without versions or sequences, out-of-order delivery can
  regress data - use invalidation.
- Payload deserialization failures happen in the transport (SignalR) before the synchronizer sees the event.
