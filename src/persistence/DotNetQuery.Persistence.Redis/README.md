# DotNetQuery.Persistence.Redis

A Redis-backed [`IQueryPersister`](../../DotNetQuery/Persistence/IQueryPersister.cs) for DotNetQuery, built on
[StackExchange.Redis](https://stackexchange.github.io/StackExchange.Redis/). Every persisted query is a
field in a single Redis hash, so it works well for shared or server-side caches read by multiple
instances.

```csharp
using DotNetQuery;
using DotNetQuery.Persistence;

// Owns the connection (disposed with the persister)
await using var persister = new RedisQueryPersister("localhost:6379");

// Or reuse an existing multiplexer (left open on dispose)
await using var persister2 = new RedisQueryPersister(sharedMultiplexer, hashKey: "myapp:dnq");

var manager = new QueryPersistenceManager(client, persister)
    .Register<List<Todo>>(QueryKey.Create("todos"));

await manager.RestoreAllAsync();   // on startup
await manager.PersistAllAsync();   // on shutdown / periodically
```

## Synchronizing server instances

The package also carries `RedisQuerySyncChannel`, an `IQuerySyncChannel` over Redis pub/sub, so the caches of several
server instances stay consistent (a write handled by one instance reaches the others):

```csharp
using DotNetQuery.Synchronization;

var channel = await RedisQuerySyncChannel.CreateAsync(multiplexer, "myapp:dnq-sync");
using var sync = new QuerySynchronizer(client, channel).RegisterPrefix(CatalogQueries.All, AppJson.Default.Product).Start();
```

See [Advanced scenarios](../../../docs/advanced-scenarios.md#several-server-instances-redis-pubsub).

## Running Redis for local development / tests

The integration tests connect to the endpoint in the `DOTNETQUERY_REDIS` environment variable
(default `localhost:6379`) and **skip** when no server is reachable. To exercise them, start a Redis
server first - the simplest is Docker:

```bash
docker run --rm -p 6379:6379 redis:7-alpine
```

Then run the tests normally:

```bash
dotnet test tests/DotNetQuery.Tests --filter FullyQualifiedName~Redis
# against a non-default server:
DOTNETQUERY_REDIS=my-redis-host:6379 dotnet test tests/DotNetQuery.Tests --filter FullyQualifiedName~Redis
```

## Notes

- The `ConnectionMultiplexer` is thread-safe, so no external locking is used.
- Envelopes use source-generated JSON, so the provider is trim/AOT-safe; register payload types with a
  `JsonTypeInfo<T>` on `QueryPersistenceManager` to keep the whole path reflection-free.
- Storing all queries in one hash keeps `LoadAll`/`Clear` to a single `HGETALL`/`DEL` and avoids `SCAN`.
  Change the hash key per app/tenant via the `hashKey` constructor argument.
