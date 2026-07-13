# Caching

DotNetQuery caches every query by key. Understanding two time windows - **stale time** and **cache time** - is most of what you need.

## Stale time

`StaleTime` is how long fetched data is considered *fresh*. While fresh, `FetchAsync` returns the cached value without calling your fetcher.

```csharp
await client.FetchAsync(key, fetcher, new QueryOptions
{
    StaleTime = TimeSpan.FromSeconds(30),
});
```

- **Fresh** (within stale time): served from cache, no network call.
- **Stale** (past stale time): a refetch runs; existing data is still returned while the refetch is in flight (status `Refetching`).

The default is `QueryClientOptions.DefaultStaleTime` (zero - always stale, so every access revalidates).

## Cache time

`CacheTime` is how long an **inactive** query (no observers) is retained before garbage collection. Once collected, the next access is a cold fetch.

```csharp
new QueryOptions { CacheTime = TimeSpan.FromMinutes(10) }
```

A background garbage collector sweeps expired inactive entries on `QueryClientOptions.GarbageCollectionInterval` (default 1 minute). You can also sweep manually with `client.Cache.CollectGarbage()`.

## Deduplication

Concurrent requests for the same key share a single in-flight fetch. Ten callers, one network call, one result delivered to all - and one caller cancelling does not cancel the shared work.

## Invalidation

Marking data stale and refetching active queries is invalidation. It's hierarchical: invalidating `["users"]` reaches `["users", 1]`, `["users", 2]`, and so on.

```csharp
await client.InvalidateQueriesAsync(QueryKey.Create("users"));           // by prefix
await client.InvalidateQueriesAsync(key => key.Count == 1);              // by predicate
```

Queries with active observers refetch immediately; inactive queries are simply marked stale and revalidate on next access. Invalidation also reaches registered [infinite queries](infinite-queries.md).

## Reading and seeding the cache

```csharp
var current = client.GetQueryData<User>(key);   // no fetch
client.SetQueryData(key, updatedUser);           // imperatively set, notifies observers
client.RemoveQuery(key);                          // evict, cancelling any in-flight fetch
await client.RefetchQueryAsync(key);              // force a refetch by key
```

## Persistence

To survive restarts, persist the cache with the `DotNetQuery.Persistence` package:

```csharp
var persister = new FileSystemQueryPersister(appDataDir);
var persistence = new QueryPersistenceManager(client, persister)
    .Register<List<Todo>>(QueryKey.Create("todos"));

await persistence.RestoreAllAsync();  // on startup
await persistence.PersistAllAsync();  // on shutdown / periodically
```

Providers are pluggable via `IQueryPersister` (in-memory and file-system are built in).
