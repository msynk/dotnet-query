# DotNetQuery.Sample.OfflineFirst

A console walkthrough of the offline-first feature cluster, with no UI-framework noise:

1. **Persist** — session 1 fetches online and persists the cache with `QueryPersistenceManager`.
2. **Restart offline & restore** — session 2 starts **offline**, restores the persisted data, and shows
   it immediately with no network round-trip.
3. **Work offline** — `FetchAsync` is served from the cache (the server's read counter proves no network
   call), and an "add todo" mutation is queued with `OfflineMutationQueue`.
4. **Reconnect & sync** — going online replays the queued mutation (in order) against the server, and a
   forced refetch reflects the change.

[TodoApi](TodoApi.cs) stands in for a server (with a read counter to prove offline reads hit the cache).

## Run it

```bash
dotnet run --project samples/DotNetQuery.Sample.OfflineFirst
```

The run is deterministic and exits `0` on success, so it also runs as an end-to-end smoke test in CI.
