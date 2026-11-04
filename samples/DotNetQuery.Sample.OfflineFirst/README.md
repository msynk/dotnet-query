# DotNetQuery.Sample.OfflineFirst

A console walkthrough of the offline-first feature cluster, with no UI-framework noise:

1. **Persist** - session 1 fetches online and persists the cache with `QueryPersistenceManager` (source-generated
   JSON, so the path is trim/AOT-safe).
2. **Restart offline & restore** - session 2 starts **offline**, restores the persisted data, and shows it immediately
   with no network round-trip; `FetchAsync` is served from the cache (the server's read counter proves it).
3. **Queue writes durably** - three writes go into a `PersistentMutationQueue` backed by its own store, then the app
   closes with them still queued.
4. **Restart, reconnect, replay** - session 3 restores the queue, goes online and replays it in order. Each handler
   sends the queued mutation's stable id as an idempotency key (the server applies each write once), transient
   failures are retried (`MaxAttempts`, `HttpRetryPolicy.ShouldRetry`), and a `409 Conflict` is dead-lettered by
   `OnReplayFailure` for the user to retry or discard.

[TodoApi](TodoApi.cs) stands in for a server (a read counter, idempotency keys, and a conflict for one write).

## Run it

```bash
dotnet run --project samples/DotNetQuery.Sample.OfflineFirst
```

The run is deterministic and exits `0` on success, so it also runs as an end-to-end smoke test in CI.
