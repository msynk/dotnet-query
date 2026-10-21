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
- Background triggers (focus, reconnect, refetch interval) instead *join* an in-flight fetch.
- Invalidation reaches [infinite queries](infinite-queries.md) too - they live in the same cache.

## Cancelling fetches

```csharp
await client.CancelQueriesAsync(QueryKey.Create("users"));   // prefix or predicate, like invalidation
```

A cancelled fetch restores the query's previous state (a first load returns to `Idle`, a refetch back to
`Success` with its data) and raises a `QueryCancelledEvent`. Cancel before an optimistic write so a pending
refetch cannot overwrite it (the declarative `OptimisticUpdates` do this for you).

## Reading and writing the cache

```csharp
var current = client.GetQueryData<User>(key);                         // no fetch
client.SetQueryData(key, updatedUser);                                // replace, mark fresh, notify
client.SetQueryData<List<Todo>>(listKey, todos => [.. todos ?? [], added]);  // atomic read-modify-write
client.HydrateQueryData(key, serverData, serverTimestamp);             // seed, only if newer
client.RemoveQuery(key);                                              // evict, cancelling any fetch
await client.RefetchQueryAsync(key);                                  // force a refetch by key
```

- The `SetQueryData` updater is atomic: if the entry changes while the updater runs, it is re-run against
  the newer value, so concurrent writers never lose updates. Return a new value; don't mutate the old one.
- `HydrateQueryData` keeps the original timestamp, so staleness is computed correctly for data produced
  elsewhere (prerendering, a persisted cache, a parent component).

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
