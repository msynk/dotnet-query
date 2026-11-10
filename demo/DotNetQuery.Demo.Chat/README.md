# DotNetQuery.Sample.Chat

A console showcase of two DotNetQuery features working together for a chat UI, with no UI-framework noise:

1. **Infinite query** - [Program.cs](Program.cs) paginates message *history* with
   `CreateInfiniteQuery` (`FetchAsync` for the newest page, `FetchNextPageAsync` for older pages).
2. **Real-time streaming** - `StreamIntoCacheAsync` folds a *live* message stream into the cache, and an
   observer prints each new message as it arrives - exactly what a bound `<Query>` component or MVVM view
   would react to.

The [ChatService](ChatService.cs) stands in for a backend: history pages (an HTTP/GraphQL call in a real
app) and a bounded live stream (a SignalR `HubConnection.StreamAsync<Message>` in a real app).

## Run it

```bash
dotnet run --project samples/DotNetQuery.Sample.Chat
```

Expected output: two history pages (20 messages) load, then five live messages stream in one by one. The
run is deterministic and exits `0` on success, so it also runs as an end-to-end smoke test in CI.
