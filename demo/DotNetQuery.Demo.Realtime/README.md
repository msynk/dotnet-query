# DotNetQuery.Demo.Realtime

Real-time cache synchronization end to end, in one console process: an ASP.NET Core server ([Server.cs](Server.cs))
with a SignalR hub publishes versioned, sequenced todo changes as `QueryEventEnvelope<Todo>`s and keeps their history;
a client ([Program.cs](Program.cs)) keeps its query cache in sync through `Msynk.DotNetQuery.SignalR`.

What it shows, in order:

1. **Two independent views** (a list and a detail observer) of one cache, loaded after the connection is up.
2. **Incremental updates** - another user edits a todo; one `Upsert` mapping updates the list item and the detail in
   place (no refetch), and **targeted invalidation** refetches only the stats query.
3. **An optimistic mutation racing its own broadcast** - the optimistic title shows at once, the server's event
   (newer version) arrives before the slow response and replaces it, and nothing flickers back.
4. **Disconnect, missed updates, reconnect** - a simulated outage drops the connection while two changes are published;
   on reconnect the source rejoins the workspace group (`OnConnected`) and a `Replay` recovery fetches the missed events
   from the server's history (`EventsSince`), so the list catches up without a refetch.
5. **Duplicate protection** - a redelivered old event is discarded by its event id.
6. **A follower "tab"** without its own connection catches up through `QueryEventRelay` notices (the Blazor
   `BroadcastChannel` relay works the same way; no payloads cross tabs).

## Run it

```bash
dotnet run --project demo/DotNetQuery.Demo.Realtime
```

The run is deterministic and prints `[ok]`/`[FAIL]` for every check; it exits `0` when all pass, so it also runs as an
end-to-end smoke test in CI. The simulated network (`SimulatedNetwork.cs`) and long polling exist only to make the
outage reproducible; a real app uses the default transport and handler.

See [Real-time synchronization](../../docs/realtime-sync.md) for the full guide.
