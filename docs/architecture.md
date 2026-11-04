# Architecture and lifecycle

How DotNetQuery is put together, what happens during a fetch, and the guarantees each part keeps. Read this when
you need to reason about timing (why a component re-rendered, why a fetch did not run) or before contributing to
the engine.

## The pieces

```
IQueryClient (QueryClient)
 ├─ QueryClientOptions ........ defaults, prefix defaults, limits, interceptors, mutation defaults
 ├─ QueryCache
 │   ├─ QueryEntry<T> × N ..... one per key: state, fetch cycle, observers, refetch schedule
 │   ├─ MutationCache ......... every mutation run (for inspection, scopes, retention)
 │   └─ QueryRuntime .......... clock, connectivity, concurrency limiter, rate limiter, event sink
 ├─ signal managers ........... FocusManager, OnlineManager, ResumeManager, ConnectivityProvider
 └─ diagnostics ............... DiagnosticEvent, ActivitySource, Meter, ILogger (AddDiagnosticLogging)
Mutation<TVariables, TData> .... one per operation; optimistic layers on the entries it touches
Adapters ....................... Blazor components/hooks, MVVM bindings, lifecycle classes, persistence, sync
```

- **`QueryKey`** - an immutable, hierarchical list of segments with a cached hash. A key is the identity of a query
  and a prefix selects a family (`["todos"]` matches `["todos", 5]`).
- **`QueryEntry<T>`** - the heart of the library. It owns the current `QueryState<T>`, the in-flight fetch cycle,
  the observers and the interval timer. Every client operation ends up here.
- **`QueryObserver<T>`** - a subscription to one entry; observers make a query *active*. Selectors
  (`Watch(key, select)`) and `notifyOn` filters run on the subscriber side, so an observer is only notified when
  what it looks at changed.
- **Signals** - adapters report environment changes (window focus, app resume, network) through the signal
  managers; the client refetches the stale, active queries that opted in (`RefetchOnFocus`, `RefetchOnResume`,
  `RefetchOnReconnect`).

## Lifecycle of a fetch

`client.FetchAsync(key, fetcher, options)`:

1. **Resolve options** - per-query options over prefix defaults (`SetQueryDefaults`) over client defaults.
2. **Find or create the entry** - an existing entry is found without allocating; a new one is created atomically
   (a concurrent creator wins and both use it).
3. **Decide under the entry's lock** - remember the fetcher (invalidation, intervals and signals reuse it), then:
   - disabled query: return the current state;
   - a fetch is in flight: join it (deduplication);
   - data is fresh (`StaleTime`): return the cached state - no task, closure or result is allocated for this path;
   - offline in `NetworkMode.Online`: pause (`IsPaused`) and resume on reconnect;
   - otherwise start a **fetch cycle**.
4. **Run the cycle (outside the lock)** - on the thread pool: wait for a concurrency slot (`MaxConcurrentFetches`,
   by `Priority`) and a rate-limiter permit, apply the `Timeout`, run the interceptors and the fetcher. A failure is
   classified (`ShouldRetry`, `Retry-After`) and retried after `RetryDelay`; going offline mid-retry pauses the cycle
   instead of burning attempts.
5. **Complete** - under the lock, the outcome becomes the new state (structural sharing keeps unchanged instances),
   unless the cycle was superseded (invalidation, a forced refetch, reset or removal), in which case its result is
   dropped and its waiters receive the newer cycle's outcome.
6. **Notify** - the change goes into the entry's outbox and is delivered after the lock is released (see below).

States: `Idle` -> `Loading` -> `Success` | `Error`; a refetch with data is `Refetching`; `Paused` while waiting for
connectivity. `IsStale` is computed from `DataUpdatedAt` and the stale time; invalidation marks data stale
immediately.

## Notifications and the outbox

User code - fetchers, updaters, observer handlers, cache-change handlers - never runs while an entry lock is held.
Each state transition is recorded under the lock into an ordered outbox; after the lock is released, a single
drainer per entry delivers the queued transitions to subscribers. So:

- every subscriber sees every transition exactly once, in order, even when changes race on several threads;
- a slow or throwing subscriber cannot block or fail a fetch (failures are reported as `QueryObserverErrorEvent`);
- `QueryNotifications.Batch()` defers draining until the scope ends, so a burst of writes notifies once.

Blazor components and MVVM bindings then marshal to their UI thread; MVVM bindings stamp each notification so a
late one can never roll the UI back.

## Mutations

`Mutation<TVariables, TData>.MutateAsync(variables)`:

1. Wait for its **scope** (mutations sharing a `Scope` run one at a time, in order) and for connectivity
   (`NetworkMode.Online`).
2. Apply **optimistic updates** atomically across their keys as a *layer* on each entry. Layers stack: when one
   mutation rolls back, the layers above it are re-applied on top of the restored value instead of being reverted.
3. Run the mutation function with retries.
4. On success, commit the layer; on failure, roll it back (a write that landed on top in the meantime is never
   clobbered - the key is invalidated instead).
5. Invalidate the `Invalidates` keys and run the callbacks. Every run is recorded in the `MutationCache`. While a
   query carries optimistic layers of other pending mutations, its refreshes are deferred: it is marked stale, and the
   last of those mutations to settle runs one refetch, so pending optimistic values are never replaced by server data
   that does not include them yet. A shared logical clock (`QueryRuntime.NextSequence`) orders fetch starts against
   mutation settles: an invalidation joins a refetch that started after its mutation's server call settled (it already
   includes the write) instead of restarting it.

The durable `PersistentMutationQueue` stores mutations that must survive restarts and replays them in order with a
stable idempotency key (at-least-once delivery).

## Garbage collection and memory

An entry with no observers becomes *inactive*. It is removed when its `CacheTime` has elapsed since it became
inactive - but never while it has observers, an in-flight fetch, or a fetch deferred while offline.
`MaxInactiveQueries` bounds the number of inactive entries (least recently used first), `TrimOnMemoryPressure`
trims on gen-2 garbage collections, and `Cache.Trim(n)` trims on demand. Removing a query that is still observed
resets and refetches it rather than stranding its observers.

## Persistence and synchronization

- `QueryPersistenceManager` saves registered queries (exact keys or prefixes) through an `IQueryPersister` (file
  system, SQLite, Redis, browser `localStorage`/IndexedDB), restores them with their original timestamps (stale data
  revalidates in the background), migrates payloads saved under an older `Buster`, and can encrypt them
  (`ProtectedQueryPersister`). A per-key write gate keeps an older save from overwriting a newer one.
- `QuerySynchronizer` keeps registered queries consistent across caches over an `IQuerySyncChannel` - browser tabs
  (`BroadcastChannel`), processes, or servers (Redis pub/sub).
- `PersistentMutationQueue` instances that share a store coordinate through an `IMutationQueueLock` (Web Locks in the
  browser, a lock file for processes): the holder reconciles with the store and replays.

## Blazor specifics

- The client is **scoped**: one cache per circuit (Blazor Server) or per tab (WebAssembly).
- During **prerendering**, `<Query>` and the hooks fetch, persist the state into the page
  (`PersistentComponentState`), and the interactive render hydrates it - no second request, no loading flash.
- `DotNetQueryBrowserSignals` turns `visibilitychange`, `focus` and `online`/`offline` into signals;
  `DotNetQueryNavigation` cancels fetches of pages left behind; `ResetOnUserChange` clears the cache when the
  signed-in user (or tenant) changes.

## Threading summary

| Runs on | What |
| --- | --- |
| Caller's thread | option resolution, cache lookup, the fresh-hit fast path, starting a cycle |
| Thread pool | fetch cycles, retries, interval timers, mutation functions |
| The draining thread | observer handlers and cache-change handlers (never under a lock) |
| UI thread | Blazor renders and MVVM property changes (marshalled by the adapters) |
