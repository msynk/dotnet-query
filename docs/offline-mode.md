# Offline Mode

DotNetQuery can pause fetching while offline, serve cached data, resume automatically on reconnect, and queue mutations for replay.

## Connectivity

Provide an `IConnectivityProvider`. The built-in `ConnectivityProvider` is adapter-driven — your platform code calls `SetOnline(...)` when the network state changes.

```csharp
var connectivity = new ConnectivityProvider();
using var client = new QueryClient(connectivity: connectivity);
// ... later, from a platform network callback:
connectivity.SetOnline(false);
```

With DI, the provider is registered for you and adapters (e.g. the Blazor bridge) drive it from browser `online`/`offline` events.

## What happens offline

When the client is offline and you call `FetchAsync`:

- If cached data exists, it is returned as-is (no network call).
- If there is no cached data, the query transitions to `Paused` without invoking the fetcher.

When connectivity is restored, active queries that opted in to `RefetchOnReconnect` (the default) refetch automatically — paused queries resume, stale ones revalidate.

```csharp
connectivity.SetOnline(false);
var result = await client.FetchAsync(key, fetcher);
// result.Status == QueryStatus.Paused (or cached data if present)

connectivity.SetOnline(true);
// observed queries refetch automatically
```

## Offline mutation queue

Mutations issued while offline should be deferred, not lost. `OfflineMutationQueue` runs a mutation immediately when online, or queues it and replays queued mutations **in order** on reconnect.

```csharp
using var queue = new OfflineMutationQueue(connectivity);

await queue.EnqueueAsync(ct => api.CreateTodoAsync(todo, ct));
// offline → queued; online → runs now
```

Each queued item is a delegate closing over its variables, so the queue is fully type-safe with no boxing.

## Related signals

Beyond connectivity, DotNetQuery reacts to focus and app-resume via `IFocusManager`, `IResumeManager`, and `IOnlineManager` (see [Diagnostics](diagnostics.md) and the Blazor adapter). Per-query opt-outs are available through `RefetchOnReconnect`, `RefetchOnFocus`, and `RefetchOnResume`.
