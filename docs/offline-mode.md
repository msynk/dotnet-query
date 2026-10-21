# Offline Mode

DotNetQuery pauses network work while offline, keeps serving cached data, resumes automatically on
reconnect, and queues mutations for ordered replay - in memory or durably across restarts.

## Connectivity

Provide an `IConnectivityProvider`. The built-in `ConnectivityProvider` is adapter-driven: platform code
calls `SetOnline(...)` when the network state changes (transitions are serialized, so subscribers always
see them in order).

```csharp
var connectivity = new ConnectivityProvider();
using var client = new QueryClient(connectivity: connectivity);
// ... later, from a platform network callback:
connectivity.SetOnline(false);
```

With DI the provider is registered for you, and adapters drive it: the Blazor bridge from browser
`online`/`offline` events, `MauiConnectivityBridge` from `Connectivity.Current`.

## What happens offline

Every path that would hit the network honors connectivity - explicit fetches, invalidation, refetch
intervals, and focus/resume triggers:

- Fresh cached data is served as usual.
- Stale cached data is served as-is, and the fetch is deferred.
- With no cached data, the query moves to `Paused` without invoking the fetcher.
- If the network drops while a fetch is failing, retries stop and the fetch is deferred instead of ending
  in an error.
- An invalidation while offline discards the result of any fetch still in flight (it would be outdated).

On reconnect, every deferred ("paused") fetch resumes - even for queries nobody is observing - and active
stale queries that opt in to `RefetchOnReconnect` (the default) revalidate. Entries with a deferred fetch
are never garbage collected.

```csharp
connectivity.SetOnline(false);
var result = await client.FetchAsync(key, fetcher);   // result.IsPaused (or cached data if present)

connectivity.SetOnline(true);                         // the fetch resumes automatically
```

## Offline mutation queue (in memory)

`OfflineMutationQueue` runs a mutation immediately when online and nothing is queued ahead of it;
otherwise it queues it and replays the queue **in order** on reconnect.

```csharp
using var queue = new OfflineMutationQueue(connectivity);
queue.PendingChanged += () => UpdateBadge(queue.PendingCount);   // "3 changes waiting to sync"
queue.MutationFailed += error => logger.LogWarning(error, "A queued change was rejected");

await queue.EnqueueAsync(ct => api.CreateTodoAsync(todo, ct));
```

- Writes always reach the server in the order they were made, including writes enqueued while a replay is
  running.
- A mutation that fails because connectivity dropped again stays at the head of the queue.
- A mutation that fails while online is dropped (one bad request cannot block the queue) and reported via
  `MutationFailed`. `FlushAsync` never throws for mutation failures.

## Durable offline queue (survives restarts)

Closures can't outlive the process, so `PersistentMutationQueue` (namespace `DotNetQuery.Persistence`) stores
mutations as data - a handler key plus serializable variables - in any `IQueryPersister`:

```csharp
var queue = new PersistentMutationQueue(new IndexedDbQueryPersister(js, storeName: "mutations"), connectivity)
    .RegisterHandler<NewTodo>("todos/add", (todo, ct) => api.CreateTodoAsync(todo, ct), AppJson.Default.NewTodo)
    .RegisterHandler<int>("todos/delete", (id, ct) => api.DeleteTodoAsync(id, ct), AppJson.Default.Int32);

await queue.RestoreAsync();                                  // at startup, after registering handlers
await queue.EnqueueAsync("todos/add", new NewTodo("Buy milk"));
```

- Every mutation is written to the store before it runs and removed only after it completes, so a crash
  mid-request replays it on the next start - make handlers idempotent (for example with a client-generated
  id).
- Replay order is exactly enqueue order, across restarts.
- Use a store dedicated to the queue, so clearing the query cache store cannot drop queued writes.
- Combine with `OptimisticUpdates` on the UI side for instant feedback while offline.

## Related signals

Beyond connectivity, DotNetQuery reacts to focus and app-resume via `IFocusManager`, `IResumeManager`,
and `IOnlineManager`. Per-query opt-outs are available through `RefetchOnReconnect`, `RefetchOnFocus`,
and `RefetchOnResume`.
