# DotNetQuery.Persistence.Redis

A Redis-backed [`IQueryPersister`](../DotNetQuery.Persistence/IQueryPersister.cs) for DotNetQuery, built on
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

## Running Redis for local development / tests

The integration tests connect to the endpoint in the `DOTNETQUERY_REDIS` environment variable
(default `localhost:6379`) and **skip** when no server is reachable. To exercise them, start a Redis
server first - the simplest is Docker:

```bash
docker run --rm -p 6379:6379 redis:7-alpine
```

Then run the tests normally:

```bash
dotnet test tests/DotNetQuery.Core.Tests --filter FullyQualifiedName~Redis
# against a non-default server:
DOTNETQUERY_REDIS=my-redis-host:6379 dotnet test tests/DotNetQuery.Core.Tests --filter FullyQualifiedName~Redis
```

## Notes

- The `ConnectionMultiplexer` is thread-safe, so no external locking is used.
- Data is stored as JSON (via `QueryPersistenceManager`), so this package is **not** AOT/trim-safe.
- Storing all queries in one hash keeps `LoadAll`/`Clear` to a single `HGETALL`/`DEL` and avoids `SCAN`.
  Change the hash key per app/tenant via the `hashKey` constructor argument.
