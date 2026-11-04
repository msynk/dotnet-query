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

## Network modes

`NetworkMode` (per query via `QueryOptions.NetworkMode`, per mutation via `MutationOptions.NetworkMode`, or
the client defaults `DefaultNetworkMode` / `DefaultMutationNetworkMode`) chooses how connectivity is honored:

| Mode | Queries | Mutations |
| --- | --- | --- |
| `Online` (query default) | Pause while offline, resume on reconnect (the behavior above) | Wait (pending, `IsPaused`) until online before running and before each retry; optimistic updates apply immediately |
| `Always` (mutation default) | Ignore connectivity: fetch and retry as if online (local or in-process data sources) | Run and fail as before |
| `OfflineFirst` | Run the first attempt even offline (an HTTP cache or service worker may answer it), pause retries until online | Run the first attempt, wait for connectivity before retrying |

```csharp
// Writes made offline wait for the network instead of failing:
builder.Services.AddDotNetQuery(o => o.DefaultMutationNetworkMode = NetworkMode.Online);

// A query served by a Blazor WebAssembly service worker cache:
new QueryOptions { NetworkMode = NetworkMode.OfflineFirst }
```

A mutation waiting for connectivity lives only in memory; use the durable queue below for writes that must
survive a restart. Cancelling a waiting mutation rolls back its optimistic updates.

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

### Delivery guarantees

Replay is **at-least-once**, never exactly-once: a crash (or a lost response) after the server applied a write
but before the queue removed it replays that write. Exactly-once requires the server's cooperation, so every
queued mutation carries a stable id that survives restarts and replays - send it as an idempotency key and have
the server ignore ids it has already applied:

```csharp
queue.RegisterHandler<NewTodo>("todos/add",
    (todo, mutation, ct) => api.CreateTodoAsync(todo, idempotencyKey: mutation.Id, ct),   // e.g. an Idempotency-Key header
    AppJson.Default.NewTodo);
```

### Retries, expiration and dead letters

```csharp
var queue = new PersistentMutationQueue(store, connectivity, new PersistentMutationQueueOptions
{
    MaxAttempts = 5,                                  // per replay, while online (default 1)
    RetryDelay = RetryPolicy.Exponential(TimeSpan.FromSeconds(1)),
    ShouldRetry = HttpRetryPolicy.ShouldRetry,        // a 400/409 fails at once, a 503 is retried
    MaxAge = TimeSpan.FromDays(2),                    // never replay writes older than this
    UseDeadLetterQueue = true,                        // keep permanent failures instead of dropping them
});

foreach (var dead in queue.DeadLetters) { /* show "N changes could not be saved" with dead.Error */ }
await queue.RetryDeadLetterAsync(id);                 // back to the end of the queue, same id
await queue.DiscardDeadLetterAsync(id);
```

- Retries keep the order: later mutations wait while one is retried. Losing connectivity mid-retry keeps the
  mutation at the head for the next reconnect.
- An expired mutation fails with `MutationExpiredException`. Permanent failures and expirations are reported
  through `MutationFailed`; with `UseDeadLetterQueue` they are also stored durably (and restored by
  `RestoreAsync`).

### Conflicts and authentication during replay

`OnReplayFailure` decides per failure - for example dead-letter conflicts for the user to resolve, and pause the
queue when the session expired:

```csharp
new PersistentMutationQueueOptions
{
    UseDeadLetterQueue = true,
    OnReplayFailure = (mutation, error, attempt) =>
        HttpRetryPolicy.IsConflict(error) ? ReplayDecision.Fail                                       // 409 / 412
        : error is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } ? ReplayDecision.Pause
        : ReplayDecision.Default,                                                                    // retry options
};
```

`Pause` keeps the mutation at the head and pauses replay until `ResumeAsync()` (after the user signs in again);
`Fail` dead-letters (or drops) it; `Drop` drops it; `Retry` retries within `MaxAttempts`.

### Several tabs or processes sharing the queue

Without coordination, every instance that restores the same store (two browser tabs over one IndexedDB store, two
windows of a desktop app over one directory) replays every queued write. Give the queue a `ReplayLock`: an instance
replays only while it holds the lock and first reconciles its list with the store, so each write is sent once - by
whichever instance gets there first - and writes queued in one instance are replayed by another if needed.

```csharp
// Blazor WebAssembly: the browser's Web Locks API, shared by the tabs of the origin.
var queue = new PersistentMutationQueue(new IndexedDbQueryPersister(js, storeName: "mutations"), connectivity,
    new PersistentMutationQueueOptions { ReplayLock = new WebLocksMutationQueueLock(js, "todos-queue") });

// Desktop apps with several processes: an exclusively opened lock file next to the store.
var queue = new PersistentMutationQueue(new FileSystemQueryPersister(dir), connectivity,
    new PersistentMutationQueueOptions { ReplayLock = new FileMutationQueueLock(Path.Combine(dir, "queue.lock")) });
```

`RestoreAsync` reconciles with the store as well - entries another instance added appear, entries it replayed or
removed disappear - so call it again (for example when a tab becomes visible) to refresh `Pending` and `DeadLetters` in
an instance that did not replay them. Queued mutation ids include a per-instance part, so instances never overwrite
each other's entries. Implement `IMutationQueueLock` over a distributed lock for other topologies.

### Managing the queue

- `Pause()` / `ResumeAsync()` - hold replay while the user is signed out (writes keep being stored), resume
  once credentials are valid again.
- `RemoveAsync(id)` - drop a queued mutation the user cancelled (not one that is replaying right now).
- `ClearAsync()` - drop everything queued and dead-lettered, for example when a different user signs in.
- `Pending`, `PendingCount`, `PendingChanged`, `DeadLetters` - for "N changes waiting to sync" UIs.

## Blazor WebAssembly and installable apps (PWA)

A WebAssembly app keeps working offline when its data and its pending writes live in the browser:

1. **Data** - persist the queries the app needs offline to IndexedDB (`IndexedDbQueryPersister`, or
   `localStorage` for small data) and restore them on startup, before the first render:

   ```csharp
   var persistence = new QueryPersistenceManager(client, new IndexedDbQueryPersister(js),
           new QueryPersistenceOptions { MaxAge = TimeSpan.FromDays(7), Buster = "v3" })
       .RegisterPrefix(TodoQueries.All, AppJson.Default.ListTodo);
   await persistence.RestoreAllAsync();
   persistence.StartAutoPersist();
   ```

2. **Writes** - queue them in a `PersistentMutationQueue` backed by IndexedDB (a store of its own), so a write made
   offline survives closing the tab and replays (in order, with its idempotency key) when the app is online again.
   Give it a `WebLocksMutationQueueLock` so that with the app open in several tabs only one of them replays each write
   (see above).
3. **Connectivity** - `AddDotNetQueryBlazor()` already reports the browser's `online`/`offline` events, so queries
   pause instead of failing and refetch on reconnect.
4. **Service worker** - the Blazor PWA template's service worker caches the app itself (the `_framework` files);
   leave API responses to DotNetQuery rather than caching them in the service worker too, or the cache would serve
   stale responses that DotNetQuery believes are fresh. If the service worker does serve API responses offline,
   use `NetworkMode.OfflineFirst` for those queries so they still try the network while the browser reports
   offline.

## Related signals

Beyond connectivity, DotNetQuery reacts to focus and app-resume via `IFocusManager`, `IResumeManager`,
and `IOnlineManager`. Per-query opt-outs are available through `RefetchOnReconnect`, `RefetchOnFocus`,
and `RefetchOnResume`.
