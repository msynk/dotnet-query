# DotNetQuery.Persistence.Sqlite

A SQLite-backed [`IQueryPersister`](../DotNetQuery/Persistence/IQueryPersister.cs) for DotNetQuery, built
on [Microsoft.Data.Sqlite](https://learn.microsoft.com/dotnet/standard/data/sqlite/). Stores each
persisted query as a row in a single `dotnetquery_cache` table for durable cross-session caching.

```csharp
using DotNetQuery;
using DotNetQuery.Persistence;

// File-backed (durable across app restarts)
await using var persister = SqliteQueryPersister.ForFile("cache.db");

// or any Microsoft.Data.Sqlite connection string
await using var inMemory = new SqliteQueryPersister("Data Source=:memory:");

var manager = new QueryPersistenceManager(client, persister)
    .Register<List<Todo>>(QueryKey.Create("todos"));

await manager.RestoreAllAsync();   // on startup
await manager.PersistAllAsync();   // on shutdown / periodically
```

## Notes

- **One connection, held open.** The persister keeps a single connection open for its lifetime (required
  so an in-memory database survives between calls) and serializes access with a semaphore. **Dispose it**
  (`await using` / `using`) when done - disposal also clears the connection pool so the database file is
  released and can be deleted or replaced immediately.
- **Trim/AOT analysis is clean** for this package (it stores plain strings). Register payload types on
  `QueryPersistenceManager` with a `JsonTypeInfo<T>` to keep the whole persistence path reflection-free.
- The native SQLite bundle is pinned to a patched `SQLitePCLRaw` release to avoid a known advisory in the
  version Microsoft.Data.Sqlite pulls transitively.
