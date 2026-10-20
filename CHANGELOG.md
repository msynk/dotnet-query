# Changelog

All notable changes to DotNetQuery. The project is pre-1.0; public API baselines live in each package's
`PublicAPI.Unshipped.txt`.

## 0.8.0 (unreleased)

A correctness, Blazor and developer-experience overhaul driven by a full review of the library.

### Fixed - engine correctness

- A cancelled fetch (opt-in `CancelInFlightWhenUnobserved`, entry removal) left the query stuck in
  `Loading`/`Refetching` forever. Cancelled and superseded fetches now restore the previous state.
- An exception thrown by a `StateChanged` handler (for example a UI render failure) or a `DiagnosticEvent`
  handler was treated as a fetch failure - marking the query as errored and **re-running the fetcher**
  through the retry loop. Subscriber exceptions are now isolated and reported as `QueryObserverErrorEvent`.
- Invalidating a query while a fetch was in flight joined that fetch, so a mutation followed by an
  invalidation could still show pre-mutation data. Invalidation and explicit refetches now cancel the
  outdated fetch and hand its waiters the fresh result; background triggers still deduplicate.
- Observer notifications from different threads could be delivered out of order, leaving observers on an
  older state. Notifications are now queued and delivered in order, exactly once.
- Offline handling only covered `FetchAsync`: invalidation, refetch intervals and focus/resume triggers
  still hit the network (failing with retries) while offline, paused fetches of unobserved queries never
  resumed, and connectivity loss mid-retry burned all retries. Every path now pauses offline and resumes on
  reconnect; deferred fetches are never garbage collected.
- Garbage collection could remove an entry concurrently with `Watch`/`FetchAsync` (orphaning the observer)
  or while a fetch was in flight. Collection is now atomic with respect to both.
- Mutations: a throwing `OnSuccess`/`OnSettled` re-ran the mutation function (a duplicate POST); a throwing
  `OnMutate` left the mutation `Pending` forever; cancellation skipped the optimistic rollback. All fixed,
  and a write the server accepted is never rolled back or reported as cancelled.
- `QueryKey.Create<T>()` produced the same segment for every generic type (`List<A>` and `List<B>` both
  became ``"List`1"``), so unrelated queries collided. Generic arguments are now included.
- `QueryKey.ToString()` was culture-sensitive and did not escape quotes, so persisted keys could differ
  between cultures or collide. It is now invariant and unambiguous. List segments now compare structurally.
- `QueryState.IsSuccess` was `false` for a successful fetch that returned `null`.
- `Timeout.InfiniteTimeSpan` as a stale or cache time meant "always stale"/"collect immediately"; it now
  means "never".
- `SetQueryData` reset a query's configured cache time to the client default.
- Restored persisted data was treated as freshly fetched; it now keeps its original timestamp.
- `OfflineMutationQueue` lost the mutation that failed during a replay and could run new mutations ahead of
  queued ones.
- Build: removed the `Microsoft.SourceLink.GitHub` 8.0.0 package (SourceLink ships in the SDK), whose
  vulnerable transitive `Microsoft.Build.Tasks.Git` failed every build with NU1902; lifted the test-only
  AngleSharp dependency past a flagged version.

### Fixed - Blazor

- `AddDotNetQueryBlazor` registered a **singleton** client, so in Blazor Server every user shared one cache
  (and one user's browser going offline paused queries for everyone). It now registers a scoped client:
  one private cache per circuit (per tab in WebAssembly).
- `BrowserSignalService` was disposed twice (by the component and by DI), signalled a reconnect on startup
  (refetching every active query), and could leak its JS registration when disposed during startup.
- The sample app served an empty `blazor.web.js` outside the Development environment and did not load the
  DevTools panel styles.

### Added

- **Query definitions:** `QueryDefinition<T>` (key + fetcher + options) with definition overloads for every
  client operation, `EnsureQueryDataAsync`, and `WithOptions`.
- **Cache APIs:** atomic `SetQueryData(key, updater)`, `HydrateQueryData`, `CancelQueriesAsync`,
  `QueryCache.Changed`, `QueryCache.Describe(key)`, `QueryClient.Options`/`IsOnline`.
- **Results:** synchronous `Loading` transition (no idle first render), `IsRefetching`, `IsLoadingError`,
  `IsRefetchError`, `IsPaused`, `DataUpdatedAt`, `FailureCount`, and `KeepPreviousData` placeholder results.
- **Mutations:** declarative `OptimisticUpdates` (cancel, snapshot, apply, exact rollback, conflict-safe),
  `Invalidates`, `MutationKey`, mutation ids and variables in diagnostics, latest-run state tracking.
- **Infinite queries** now live in the cache (`InfiniteData<TParam, TPage>`): shared per key, invalidated,
  garbage collected, inspectable, persistable and hydratable; `QueryOptions` support; refetches re-derive
  cursors; next-page fetches supersede background refetches.
- **Blazor:** multi-targets .NET 8 and .NET 10; `<Query>` `Definition`, `Loading`/`Error` templates,
  `KeepPreviousData`, dependent-query `Enabled` handling, automatic prerender hydration
  (`PersistState`, `QueryStateHydration`); hook-style `UseQuery`/`UseMutation`/`UseInfiniteQuery` on
  `DotNetQueryComponentBase` with per-call-site subscriptions and `ErrorBoundary` integration.
- **DevTools:** mutation history, cached data previews, cancelled-fetch and in-flight stats, live refresh
  on any cache change; the Blazor panel gained tabs, filtering, a details pane, cancel actions and a
  floating mode. The CLI renders mutations.
- **Persistence:** `QueryPersistenceOptions` (`MaxAge`, `Buster`), auto-persist, `JsonTypeInfo<T>`
  registration, error reporting, per-key write ordering, atomic file writes, corruption tolerance,
  `PersistedQueryJson`, and the durable `PersistentMutationQueue`.
- **Offline queue:** ordered enqueue during replay, `PendingChanged`, `MutationFailed`.
- **Trimming/NativeAOT:** Persistence (all providers), Http and GraphQL are now `IsAotCompatible`, with
  `JsonTypeInfo<T>` overloads (`JsonQuery`, `GraphQLQuery`, ...) and annotated reflection overloads.
- **DI:** `AddDotNetQuery(ServiceLifetime, ...)` and `IDotNetQueryBuilder.ClientLifetime`.
- **MVVM:** definition constructors, `ChangeQuery` (re-key with optional keep-previous-data), more state
  properties, UI-thread-consistent state, `ObservableMutation` is disposable.
- Diagnostics: `QueryCancelledEvent`, `QueryObserverErrorEvent`, mutation activities, cancelled/failed
  counters and mutation duration metrics.

### Changed

- Fetchers run on the thread pool, never on the caller's UI synchronization context.
- `FetchAsync(..., forceRefetch: true)` cancels an in-flight fetch instead of joining it.
- `RefetchQueryAsync` never throws for fetch failures.
- `OfflineMutationQueue.FlushAsync` no longer throws when a replayed mutation fails (see `MutationFailed`).
- `InfiniteQuery.FetchAsync` serves fresh cached pages (and refetches loaded pages when stale) instead of
  always resetting to the first page; `InfiniteQueryOptions` gained `QueryOptions`.
- `QueryDescriptor` gained `Data` and `StaleTime`; `QueryDevToolsStats` gained fetching/cancelled/failed/pending counts.
