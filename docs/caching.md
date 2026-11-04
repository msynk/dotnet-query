# Caching

DotNetQuery caches every query by key. Understanding two time windows - **stale time** and **cache time** -
is most of what you need.

## Stale time

`StaleTime` is how long fetched data is considered *fresh*. While fresh, `FetchAsync` returns the cached
value without calling your fetcher.

```csharp
await client.FetchAsync(key, fetcher, new QueryOptions
{
    StaleTime = TimeSpan.FromSeconds(30),
});
```

- **Fresh** (within stale time): served from cache, no network call.
- **Stale** (past stale time, invalidated, or failed): a refetch runs; existing data is still returned while
  the refetch is in flight (status `Refetching`).

The default is `QueryClientOptions.DefaultStaleTime` (zero - always stale, so every access revalidates).
Use `Timeout.InfiniteTimeSpan` for data that only changes when you invalidate it.

## Cache time

`CacheTime` is how long an **inactive** query (no observers) is retained before garbage collection. Once
collected, the next access is a cold fetch.

```csharp
new QueryOptions { CacheTime = TimeSpan.FromMinutes(10) }   // Timeout.InfiniteTimeSpan: never collect
```

A background collector sweeps expired inactive entries every `QueryClientOptions.GarbageCollectionInterval`
(default 1 minute; `Timeout.InfiniteTimeSpan` disables it). You can also sweep with
`client.Cache.CollectGarbage()`. Entries with observers, a fetch in flight, or a fetch paused while offline
are never collected.

## Defaults by key prefix

Configure whole families of queries at once. Layers apply from general to specific - client defaults, then
each matching prefix from shortest to longest, then the query's own options - and each layer overrides only
the properties it sets:

```csharp
builder.Services.AddDotNetQuery(o => o
    .SetQueryDefaults(QueryKey.Create("reference-data"), new QueryOptions { StaleTime = TimeSpan.FromHours(1) })
    .SetQueryDefaults(QueryKey.Create("live"), new QueryOptions { RefetchInterval = TimeSpan.FromSeconds(5) }));
```

## Deduplication

Concurrent requests for the same key share a single in-flight fetch. Ten callers, one network call, one
result delivered to all - and one caller cancelling its wait does not cancel the shared work. Fetchers run
on the thread pool, never on a UI synchronization context.

## Invalidation

Invalidation marks data stale and refetches active queries. It's hierarchical: invalidating `["users"]`
reaches `["users", 1]`, `["users", 2]`, and so on.

```csharp
await client.InvalidateQueriesAsync(QueryKey.Create("users"));           // by prefix
await client.InvalidateQueriesAsync(key => key.Count == 1);              // by predicate
await client.InvalidateQueriesAsync(UserQueries.All);                     // via a definition's key
```

- Active (observed), enabled queries refetch immediately; inactive ones revalidate on next access.
- A fetch already in flight for an invalidated query is **cancelled and restarted**: it may have started
  before the change that prompted the invalidation, so its result could be outdated. Callers awaiting the
  cancelled fetch receive the new result.
- A mutation's `Invalidates` knows when its server call settled: a refetch that started after that (for example the
  one another mutation's invalidation just started) already includes the write, so it is joined instead of restarted,
  and a refetch that already completed after it counts too. Several mutations settling together therefore refetch
  a query once.
- Background triggers (focus, reconnect, refetch interval) instead *join* an in-flight fetch.
- Invalidation reaches [infinite queries](infinite-queries.md) too - they live in the same cache.

### Invalidation dependencies

When some queries are derived from others (statistics computed from todos, a dashboard built from both),
declare it once instead of listing every dependent at each mutation:

```csharp
builder.Services.AddDotNetQuery(o => o
    .AddInvalidationDependency(QueryKey.Create("todos"), QueryKey.Create("stats"))
    .AddInvalidationDependency(QueryKey.Create("stats"), QueryKey.Create("dashboard")));

await client.InvalidateQueriesAsync(QueryKey.Create("todos", 7));   // also invalidates stats and dashboard
```

Dependencies are followed transitively (cycles are fine) and apply even when nothing under the invalidated key is
cached; active dependents refetch like any invalidated query.

## Cancelling fetches

```csharp
await client.CancelQueriesAsync(QueryKey.Create("users"));   // prefix, predicate or QueryFilter, like invalidation
```

A cancelled fetch restores the query's previous state (a first load returns to `Idle`, a refetch back to
`Success` with its data) and raises a `QueryCancelledEvent`. Cancel before an optimistic write so a pending
refetch cannot overwrite it (the declarative `OptimisticUpdates` do this for you).

## Filters and bulk operations

A `QueryFilter` selects cached queries by key (prefix, or exact with `Exact = true`), activity
(`Type = Active / Inactive`), staleness (`Stale`), fetch state (`Fetching`), `Status`, or any `Predicate`
over the query's `QueryDescriptor`. Unset criteria match everything; set criteria combine with AND.

```csharp
var todos = QueryFilter.ForKey(TodoQueries.All);

await client.InvalidateQueriesAsync(todos, RefetchType.All);                 // also refetch inactive ones
await client.InvalidateQueriesAsync(todos, RefetchType.None);                // only mark stale
await client.RefetchQueriesAsync(todos with { Stale = true });               // refetch now, regardless of observers
await client.CancelQueriesAsync(new QueryFilter { Fetching = true });        // stop everything in flight
await client.ResetQueriesAsync(QueryFilter.ForKey(QueryKey.Create("tenant", oldId)));  // back to initial state
client.RemoveQueries(new QueryFilter { Status = QueryStatus.Error, Type = QueryTypeFilter.Inactive });
var syncing = client.CountFetching();                                          // global "syncing" badge
var errors = client.Cache.FindAll(new QueryFilter { Status = QueryStatus.Error });   // inspect
```

| Operation | What it does |
| --- | --- |
| `InvalidateQueriesAsync(filter, refetchType)` | Marks matches stale; refetches `Active` (default), `Inactive`, `All` or `None` of them, cancelling outdated fetches |
| `RefetchQueriesAsync(filter)` | Refetches matches with their last fetcher regardless of staleness; skips disabled and never-fetched queries |
| `CancelQueriesAsync(filter)` | Cancels in-flight fetches, restoring each query's previous state |
| `ResetQueriesAsync(filter)` | Cancels, discards data and error (back to `Idle`), then refetches the observed ones |
| `RemoveQueries(filter)` | Evicts matches from the cache; returns how many were removed |
| `CountFetching(filter)` | How many matches are fetching right now |
| `Cache.FindAll(filter)` | Point-in-time `QueryDescriptor`s of the matches |

## Reading and writing the cache

```csharp
var current = client.GetQueryData<User>(key);                         // no fetch
client.SetQueryData(key, updatedUser);                                // replace, mark fresh, notify
client.SetQueryData<List<Todo>>(listKey, todos => [.. todos ?? [], added]);  // atomic read-modify-write
client.HydrateQueryData(key, serverData, serverTimestamp);             // seed, only if newer
client.RemoveQuery(key);                                              // evict, cancelling any fetch (observed: reset + refetch)
await client.RefetchQueryAsync(key);                                  // force a refetch by key
```

- The `SetQueryData` updater is atomic: if the entry changes while the updater runs, it is re-run against
  the newer value, so concurrent writers never lose updates. Return a new value; don't mutate the old one.
- `HydrateQueryData` keeps the original timestamp, so staleness is computed correctly for data produced
  elsewhere (prerendering, a persisted cache, a parent component).
- Removing (`RemoveQuery`, `RemoveQueries`, `Clear`) a query that is still observed does not drop the entry -
  that would leave its observers on a dead entry that never updates again. Its data is discarded instead (any
  in-flight fetch is cancelled and can no longer land) and it is refetched for its observers. Unobserved
  queries are evicted.

## Atomic multi-query writes

`SetQueriesData` applies several writes as one transaction: observers and readers see none or all of them,
concurrent writes to the involved keys are never lost (updaters are re-run against newer data), and an updater
that throws writes nothing. The returned commit rolls everything back exactly, skipping (and reporting) keys
that changed again since:

```csharp
var commit = client.SetQueriesData(tx => tx
    .Update<List<Todo>>(TodoKeys.List, todos => [.. todos ?? [], added])
    .Set(TodoKeys.ById(added.Id), added)
    .Update<int>(TodoKeys.Count, count => count + 1));

var conflicts = commit.Rollback();   // keys that were written again meanwhile - invalidate them to reconcile

// Patch every cached list of a type that matches a filter, atomically:
client.SetQueriesData<List<Todo>>(QueryFilter.ForKey(TodoKeys.All), todos => todos!.Select(t => t.Id == id ? updated : t).ToList());
```

Mutations use the same mechanism for their declarative `OptimisticUpdates`.

## Snapshots

`Cache.CreateSnapshot(filter)` captures queries (data, timestamps, status, staleness) in memory;
`snapshot.Restore()` puts them back exactly, detaching any fetch in flight so an outdated result cannot land on
top. `Restore(removeOthers: true)` also removes queries that were not in the snapshot. Use it to undo a batch of
local edits, reset a demo, or step back in time while debugging (the DevTools panel builds on it).

```csharp
var before = client.Cache.CreateSnapshot(QueryFilter.ForKey(TodoQueries.All));
// ... risky local edits ...
before.Restore();
```

## Batching notifications

Inside `QueryNotifications.Batch()`, observers of the queries written on the current asynchronous flow are
notified when the scope ends - one wave instead of one notification per write, in order and without losing
transitions. Batches nest; writes made on other flows are not delayed.

```csharp
using (QueryNotifications.Batch())
{
    foreach (var todo in imported) client.SetQueryData(TodoKeys.ById(todo.Id), todo);
}
```

## Structural sharing

With `StructuralSharing` enabled (per query, or `DefaultStructuralSharing` on the client), a refetch that
returns data equal to what is cached keeps the cached instance, and a refetch that changed only some items of
a list or dictionary returns a new collection that reuses every unchanged item. References stay stable, so
selectors skip recomputation, MVVM bindings do not re-announce `Data`, and UIs can skip unchanged parts.

```csharp
builder.Services.AddDotNetQuery(o => o.DefaultStructuralSharing = true);
```

Equality is `Equals` - records, strings, primitives and anything with value equality - applied element by
element through lists, arrays and dictionaries (unchanged items are swapped into the freshly fetched,
writable collection; read-only collections are only shared when entirely equal). It is **off by default**
because it is only correct when `Equals` compares a type's full value: do not enable it for types whose
`Equals` compares identity (for example an entity's `Id`). A record that holds a collection compares that
collection by reference; override its `Equals` (or use a list as the cached type) to share it. A throwing
`Equals` never fails a fetch - sharing is simply skipped. Refetches still update `DataUpdatedAt` and notify.

Sharing applies to every way data enters the cache, not only fetches: `SetQueryData` (values and updaters),
hydration (prerendered or persisted data) and data synchronized from other caches. A query follows its own
`StructuralSharing` option once it has been fetched, and the client's `DefaultStructuralSharing` before that. As
with fetched data, the written collection may have unchanged items swapped for the cached instances in place, so
do not keep mutating a collection after handing it to the cache.

## Selectors and fine-grained notifications

`Watch` overloads take a selector and/or the state properties a consumer cares about, so it is notified -
and re-renders - only when those change:

```csharp
// Data is select(cached data); recomputed only when the cached instance changes.
using var openCount = client.Watch(TodoQueries.All, (List<Todo> todos) => todos.Count(t => !t.Done),
    QueryStateProperties.Data | QueryStateProperties.Error);

// No selector: ignore background refetch status flips, only react to data or errors.
using var todos = client.Watch<List<Todo>>(TodoQueries.All, QueryStateProperties.Data | QueryStateProperties.Error);
```

- `QueryStateProperties.Data` fires when the (selected) data changes by structural equality (or by the
  `comparer` you pass), so a selected list that is element-wise equal does not notify.
- `CurrentState` is always up to date, even for changes that did not raise `StateChanged`.
- A throwing selector puts the observer in `Error` with that exception and keeps the last good selection;
  the next good data recovers it. Selectors run outside cache locks, once per data instance.

## Watching the cache

`client.Cache.Changed` reports every entry added, removed, state change and data update - the hook used by
DevTools and auto-persistence:

```csharp
client.Cache.Changed += change =>
{
    if (change.Kind == QueryCacheChangeKind.DataUpdated) { /* ... */ }
};
```

## Persistence

To survive restarts, persist registered queries with `QueryPersistenceManager`
(namespace `DotNetQuery.Persistence`):

```csharp
var persistence = new QueryPersistenceManager(client, new FileSystemQueryPersister(appDataDir),
        new QueryPersistenceOptions { MaxAge = TimeSpan.FromDays(7), Buster = "v3" })
    .Register(TodoQueries.List(api), AppJson.Default.ListTodo);   // source-generated: trim/AOT-safe

await persistence.RestoreAllAsync();   // on startup - keeps each entry's original timestamp
persistence.StartAutoPersist();        // save registered queries whenever their data changes
```

Restored data that is past its stale time is shown immediately and revalidated in the background.
`MaxAge` discards entries that are too old, and `Buster` ignores data written by an incompatible version.
See [Advanced Scenarios](advanced-scenarios.md#persisting-the-cache-across-sessions) for all providers.
