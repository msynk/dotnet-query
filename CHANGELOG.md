# Changelog

All notable changes to DotNetQuery. The project is pre-1.0; public API baselines live in each package's
`PublicAPI.Unshipped.txt`.

## Unreleased (0.9.0, the first release published on nuget.org)

Production-readiness work driven by the improvement plan (see `IMPROVEMENT-PLAN.md` and
`IMPLEMENTATION-STATUS.md`), on top of the 0.8.0 notes below - 0.8.0 itself was never published, so these notes and
the 0.8.0 ones together describe the first public release. All changes are additive unless listed under "Changed".

### Added

- **Query and mutation `Meta`:** `QueryOptions.Meta`, `MutationOptions.Meta` and `MutationDefaults.Meta` attach
  application data for cache-wide handlers; surfaced on `QueryDescriptor`, `MutationDescriptor`, `QueryCompletedEvent`,
  `QueryFailedEvent`, `MutationStartedEvent` and `MutationCompletedEvent`.
- **Logging:** `IDotNetQueryBuilder.AddDiagnosticLogging(options)` and `client.LogDiagnostics(logger)` write the
  diagnostic event stream to `ILogger` with source-generated `LoggerMessage`s (`QueryDiagnosticsLoggingOptions`).
- **Live DevTools for any app:** `QueryDevToolsServer` serves the DevTools export on the loopback interface (optional
  access token); the CLI gained `watch <url>` (live dashboard), `<url>` (one snapshot) and `watch demo`.
- **Durable queues shared by several instances:** `PersistentMutationQueueOptions.ReplayLock` (`IMutationQueueLock`)
  with `FileMutationQueueLock` (processes) and the Blazor `WebLocksMutationQueueLock` (browser tabs); a queue replays
  only while holding the lock and reconciles with the store first, so each queued write is sent once.
- **Redis synchronization:** `RedisQuerySyncChannel` (in `Msynk.DotNetQuery.Persistence.Redis`) keeps the caches of
  several server instances consistent over Redis pub/sub; a SignalR transport recipe in the docs.
- **`Msynk.DotNetQuery.Blazor.Server`:** `RefetchOnCircuitReconnect()` / `QueryCircuitHandler` refetch the stale
  queries on screen when a dropped Blazor Server connection comes back.
- **`Msynk.DotNetQuery.Uno`:** Uno Platform adapter (`UnoQueryExtensions` factories, `UnoQueryLifecycle`).
- **Trim-safe prerender hydration:** `AddDotNetQueryBlazor().UseHydrationJson(resolver)` and `JsonTypeInfo<T>` overloads of
  `QueryStateHydration.Persist`/`TryHydrate`; the Blazor adapter is now `IsAotCompatible` and covered by the public API
  analyzer.
- **Source generator:** `[GenerateQueries]` on classes (for example wrappers over Kiota clients), generic methods
  (type arguments become key segments, constraints are kept) and `[InfiniteQueryMethod("page")]` (key without the page
  parameter, `XPageFn`, and an `InfiniteQuery` factory).
- **Platforms:** the WPF and WinUI adapters also target .NET 8; WPF sample (`samples/DotNetQuery.Sample.Wpf`) with a CI
  self-check; WPF/WinUI lifecycle tests.
- **Engineering:** package validation, code coverage in CI, a forward-compatibility job on the newest .NET runtime, a
  trimmed Blazor WebAssembly publish check, a tag-driven release workflow (trusted publishing, provenance
  attestations, GitHub release), `eng/prepare-release.ps1`, Dependabot, `SECURITY.md`, a code of conduct and issue/PR
  templates; the DocFX API reference now includes the Razor components.

- **Retry classification:** `QueryOptions.ShouldRetry` / `QueryClientOptions.DefaultShouldRetry` /
  `MutationOptions.ShouldRetry` (`RetryPredicate`) veto retries for permanent errors; `HttpRetryPolicy`
  (`IsTransient`, `ShouldRetry`, `RespectRetryAfter`, `GetRetryAfter`). HTTP and GraphQL fetchers record the
  response's `Retry-After` on the thrown `HttpRequestException`.
- **Hierarchical defaults:** `QueryClientOptions.SetQueryDefaults(prefix, options)`.
- **Query filters and bulk operations:** `QueryFilter` (key prefix/exact, active/inactive, stale, fetching,
  status, predicate), `InvalidateQueriesAsync(filter, RefetchType)`, `RefetchQueriesAsync`,
  `CancelQueriesAsync(filter)`, `ResetQueriesAsync`, `RemoveQueries`, `CountFetching`, `QueryCache.FindAll`.
- **Structural sharing** (opt-in, `StructuralSharing` / `DefaultStructuralSharing`): equal refetches keep the
  cached instance; partially changed lists and dictionaries reuse unchanged elements. Reflection-free.
- **Selectors and fine-grained notifications:** `Watch(key, select, notifyOn, comparer)`,
  `Watch<T>(key, notifyOn)`, `QueryStateProperties`.
- **Initial and placeholder data** on `QueryDefinition<T>` (`InitialData`, `InitialDataUpdatedAt`,
  `PlaceholderData`, `WithInitialData`, `WithPlaceholderData`), `SeedInitialData`,
  `QueryResult<T>.WithPlaceholderData`; honored by `<Query>`, `UseQuery`, `ObservableQuery` and selectors.
- **Atomic multi-query writes:** `SetQueriesData(tx => ...)` and `SetQueriesData<T>(filter, updater)` with
  `QueryDataCommit.Rollback()`.
- **Mutation cache:** `QueryCache.Mutations` (`FindAll`, `Find`, `CountPending`, `Changed`, `Clear`,
  retention via `QueryClientOptions.MutationCacheTime`), `MutationDescriptor`, `MutationFilter`.
- **Mutation scopes:** `MutationOptions.Scope` runs mutations sharing a scope serially in start order;
  `MutationState.IsPaused` while waiting.
- **Blazor:** `AddDotNetQueryBlazor().ResetOnUserChange(identity?)` / `QueryAuthenticationGuard` clears the
  cache when the signed-in user (or tenant) changes; hook-style `UseQuery`/`UseInfiniteQuery` now persist and
  hydrate prerendered data and make prerendering wait for their fetches (`PersistQueryState` to opt out).

- **Network modes:** `NetworkMode` (`Online`, `Always`, `OfflineFirst`) for queries and mutations; `Online`
  mutations wait for connectivity (`IsPaused`) instead of failing. Defaults keep the previous behavior.
- **Conditional requests:** `ConditionalRequestHandler` revalidates GETs with `ETag`/`Last-Modified` and replays
  the remembered body on `304 Not Modified`.
- **Blazor templates and DevTools:** `<Query Empty IsEmpty>`; `QueryDevToolsPanel` `Enabled` and `ShowData`
  parameters, paused-mutation marker.
- **Persistence:** `RegisterPrefix<T>` persists dynamic keys and restores them on first use;
  `ProtectedQueryPersister` encrypts payloads at rest (cache and durable queue).
- **Durable queue:** retries with classification (`MaxAttempts`, `RetryDelay`, `ShouldRetry`), expiration
  (`MaxAge`, `MutationExpiredException`), a durable dead-letter queue, handlers receiving the stable mutation id
  as an idempotency key, `Pause`/`ResumeAsync`, `RemoveAsync`, `ClearAsync`.
- **Synchronization:** `QuerySynchronizer` over any `IQuerySyncChannel` keeps registered queries consistent
  across caches (last-writer-wins, rollbacks propagate, invalidations propagate); `InMemoryQuerySyncHub`; Blazor
  `AddCrossTabSync(...)` + `<DotNetQueryCrossTabSync />` over the browser `BroadcastChannel`.
- **Testing:** Playwright browser end-to-end tests for the Blazor sample (`tests/DotNetQuery.E2E`), new benchmark
  suites and a committed benchmark baseline.
- **Fetch scheduling:** `QueryOptions.Timeout` (per attempt, `TimeoutException`), `Priority` and
  `AdaptiveRefetchInterval`; `QueryClientOptions.MaxConcurrentFetches` (priority-ordered, cancellation-aware),
  `FetchRateLimiter` (`IFetchRateLimiter`, `QueryRateLimitedException`) and `Interceptors` (`IQueryInterceptor`).
- **Memory bounds:** `QueryClientOptions.MaxInactiveQueries` (least recently used inactive queries are evicted),
  `TrimOnMemoryPressure`, `QueryCache.Trim(count)`.
- **Cache snapshots and notification batching:** `QueryCache.CreateSnapshot(filter)` /
  `QueryCacheSnapshot.Restore(removeOthers)`; `QueryNotifications.Batch()` delivers the changes made inside the
  scope once it ends.
- **Invalidation dependencies:** `QueryClientOptions.AddInvalidationDependency(source, dependent)` (transitive,
  cycle-safe).
- **Overlapping optimistic updates:** optimistic writes to one key form layers; rolling back one mutation
  re-applies the updates of the others on top of the restored value instead of reverting them.
- **Mutations:** `QueryClientOptions.SetMutationDefaults(...)` / `GetMutationDefaults` (`MutationDefaults`),
  `MutationOptions.DeduplicatePending`, progress-reporting `CreateMutation((variables, progress, ct) => ...)` with
  `MutationState.Progress`.
- **HTTP:** `CircuitBreakerHandler` (`CircuitBreakerOptions`, `CircuitState`, `BrokenCircuitException`),
  `BearerTokenHandler` (single-flight token refresh on 401), `HttpRetryPolicy.IsConflict`;
  `BatchLoader<TKey, TValue>` coalesces individual lookups into batched requests; `FetchAllAsync(definitions)`.
- **Persistence and offline queue:** `QueryPersistenceOptions.Migrations` rewrite or drop payloads saved under an
  older buster; `PersistentMutationQueueOptions.OnReplayFailure` (`ReplayDecision`: retry, fail, drop, pause).
- **Diagnostics:** `QueryRetryEvent`, `OptimisticUpdateAppliedEvent`, `OptimisticUpdateRolledBackEvent`;
  `QueryClientOptions.IncludeVariablesInDiagnostics` and `DiagnosticKeyFormatter` keep secrets out of telemetry.
- **DevTools:** per-query timelines, profiles (slowest queries), key hierarchy, snapshots with restore (time
  travel), reset, offline-queue inspection (`AttachQueue`), optimistic/rollback history; the Blazor panel gained
  Snapshots and Offline queue tabs, a tree view and a status filter; the CLI renders profiles.
- **Pagination:** `InfiniteQuery.FetchPagesAsync(count)`, `PrefetchInfiniteQueryAsync(..., pages)`,
  `InfiniteData.Items(...)`, `InfiniteQueryState.PageCount`.
- **Blazor:** `InfiniteQuery.ToItemsProvider(...)` for `<Virtualize>`, `<ScrollRestoration>`,
  `<QueryClientProvider>`, `<DotNetQueryNavigation>` (cancels fetches of pages left behind), `<Query NotifyOn>`,
  `UseQueries(definitions)`.
- **Desktop and mobile:** `AppLifecycleSignals` (registered by `AddDotNetQuery`) and `NetworkConnectivityMonitor`;
  `WpfQueryLifecycle`, `WinUIQueryLifecycle`, `AvaloniaQueryLifecycle` and `MauiQueryLifecycle` refetch stale data
  when the user returns and pause queries while offline; `MauiQueryPersistence.CreateFileSystemPersister()`.
- **MVVM:** `ObservableInfiniteQuery<TParam, TPage>` (`CreateObservableInfiniteQuery` in every XAML adapter);
  bindable commands `ObservableQuery.RefetchCommand`, `ObservableMutation.MutateCommand` / `ResetCommand`,
  `ObservableInfiniteQuery.FetchNextPageCommand` / `FetchPreviousPageCommand` / `RefetchCommand`.
- **Source generator:** `[GenerateQueries]` (on an API interface, or `[assembly: GenerateQueries(typeof(IApi))]` for
  Refit/OpenAPI-generated interfaces) generates key factories, query definitions and mutations with invalidation;
  `[QueryMethod]`, `[MutationMethod]`, `[QueryIgnore]`. Ships inside the `Msynk.DotNetQuery` package; trim/AOT-safe.
- **GraphQL:** `GraphQLMutationFn` and `CreateGraphQLMutation`; `GraphQLError.Path` / `Code` and
  `GraphQLException.HasCode`.
- **Performance:** allocation-free cache lookups and fresh hits (`FetchAsync` served from the cache allocates 32 B
  instead of 488 B; `SetQueryData` with an observer 120 B instead of 216 B); new benchmark suites including a
  `MemoryCache` comparison; allocation budgets in the test suite.
- **Structural sharing** now also applies to `SetQueryData`, updaters, hydration and synchronized data.
- **Documentation:** architecture, troubleshooting, tutorials, migration and release-policy guides, and a DocFX
  site with an API reference published to GitHub Pages.
- **Samples:** the TodoApp now has per-page render modes (Interactive Server, WebAssembly and Auto, plus a
  static streaming-SSR page), an `EditForm` page, a 10,000-row virtualized list, a `QuickGrid` page and a
  multi-tenant page, and the WebAssembly page works offline (a todo added offline syncs on reconnect); the
  Playwright suite covers each of them.

### Fixed

- `PersistentMutationQueue` ids could collide between two instances writing to one store within the same clock tick
  (browsers coarsen time to milliseconds); ids now carry a per-instance part.
- `Mutation` with `DeduplicatePending`: a call made right after a run completed could join that settled run (it left
  the pending list in a continuation that could run after the caller's own); it now leaves the list before completing.
- Skipping an unchanged state compared `QueryState<T>` records, which called the cached data's `Equals` (user code)
  while the entry lock was held; it now compares the state fields without calling it.
- Blazor refetched every active query twice on a reconnect (connectivity provider and online manager both
  reported it); the duplicate signal is now coalesced.
- `DotNetQuery.Sample.Avalonia` pulled `Tmds.DBus.Protocol 0.20.0` (GHSA-xrw6-gwf8-vvr9); pinned 0.21.3.
- `PersistentMutationQueue`: a replay handler that enqueues a follow-up mutation (or calls `FlushAsync`,
  `RestoreAsync`, `RemoveAsync`, `ClearAsync` or `RetryDeadLetterAsync`) deadlocked the queue; `RestoreAsync`
  running beside such a handler did too. A retried dead letter moved back ahead of later mutations after a
  restore or restart; it now keeps its place at the end of the queue.
- `ConditionalRequestHandler` left its `If-None-Match`/`If-Modified-Since` on the request, so a request sent
  again by an outer handler (a retry, `BearerTokenHandler` after a token refresh) bypassed it and surfaced a raw
  `304`.
- `CircuitBreakerHandler`: a throwing `IsFailure` left a half-open circuit rejecting every request until
  `Reset()`; the outcome now falls back to the default classification.
- `BatchLoader`: a batch function returning `null` (or a dictionary whose lookup throws) left every waiter of
  the burst hanging; they now fail with the error. Chunks of a large burst are fetched concurrently.
- `InvalidateQueriesAsync(QueryFilter)` invalidated declared dependents of the filter's key even when an
  exact, type, staleness, fetching, status or predicate criterion matched nothing.
- `Mutation` with `DeduplicatePending` started the run (state handlers, optimistic updaters, `OnMutate`) under
  its lock; a throwing `RetryDelay` failed the mutation with the strategy's exception instead of its own error.
- `QuerySynchronizer`: messages are numbered per sender, so a transport that delivers them out of order can no
  longer apply an older value over a newer one. Prefix registrations no longer format every cached key for each
  received message.
- `QueryPersistenceManager.RestoreAllAsync` threw when two cached keys share a storage text (for example an
  `int` and a `long` segment).
- Selectors over a value type re-ran on every notification; they now re-run only when the value changes.
- Blazor: `UseQueries` never loaded a query that started disabled and was enabled later. The `Virtualize`
  items provider mapped rows to the wrong index once `MaxPages` dropped pages from the front; it now tracks
  the window and loads previous pages when scrolling back.

### Changed

- **Refreshes wait for pending optimistic updates.** While declarative optimistic updates of pending mutations are on a
  query, its invalidations, refetches (`RefetchQueryAsync`, intervals, focus/reconnect triggers) and reads of stale data
  mark it stale instead of fetching (an outdated fetch in flight is cancelled); once the last of those mutations has
  settled, one refetch runs. Concurrent mutations of one list with `Invalidates` no longer flash the server's value
  without the still-pending items, and a burst is reconciled with one refetch. A mutation whose invalidation waits for
  others completes without waiting for that refetch. Infinite next/previous page loads are not deferred.
- A rollback whose rebase races only with another layer's commit re-plans instead of falling back to a refetch; a rebase
  that cannot complete drops the layer stack so the reconciling refetch is not deferred.
- **`MutationOptions.RetryCount` and `InfiniteQueryOptions.RetryCount` are `int?`.** `null` uses the defaults; `0` now
  opts a mutation out of retries that `SetMutationDefaults` enables (previously impossible: 0 meant "unset").
- **Prerender hydration** carries a JSON string envelope (`{"updatedAt", "data"}`) instead of a serialized object;
  `QueryStateHydration.Persist<T>`/`TryHydrate<T>` without `JsonTypeInfo<T>` are annotated `[RequiresUnreferencedCode]`.
- **`PersistentMutationQueue.RestoreAsync`** reconciles with the store: entries that are no longer stored are dropped.
- `Msynk.DotNetQuery` references `Microsoft.Extensions.Logging.Abstractions`.
- `VersionPrefix` is 0.9.0; the samples reference the patched ASP.NET Core 10.0.14 packages (GHSA-g27p-qf6j-c96c).
- **Package IDs are now prefixed with `Msynk.`** (`Msynk.DotNetQuery`, `Msynk.DotNetQuery.Blazor`,
  `Msynk.DotNetQuery.Persistence.Sqlite`, `.Redis`, `.Wpf`, `.WinUI`, `.Avalonia`, `.Maui`): `DotNetQuery.Core` and
  `DotNetQuery.Blazor` on NuGet belong to an unrelated library, so the previous IDs could not all be published.
  Namespaces, assembly names and the Blazor static asset path (`_content/DotNetQuery.Blazor/...`) are unchanged -
  only `PackageReference`s need updating.
- Declarative `OptimisticUpdates` are applied (and rolled back) as one atomic transaction across keys. Two
  updates of the same key now roll back to the original value instead of triggering a refetch.
- Removing (`RemoveQuery`, `RemoveQueries`, `Clear`) a query that is still observed now resets it (data
  discarded, in-flight fetch cancelled and detached) and refetches it for its observers. Previously the entry
  was dropped and its observers were stranded on a dead entry that never updated again. `QueryClient.Dispose`
  still disposes every entry.
- `ResetQueriesAsync`/reset detaches the in-flight fetch so an outdated result can never land over the reset,
  even when the fetcher ignores cancellation.
- `ObservableQuery<T>` raises `PropertyChanged` only for properties whose value changed.
- `DotNetQuery.Blazor` now references `Microsoft.AspNetCore.Components.Authorization` (first-party).
- `DotNetQuery.Maui` now references `Microsoft.Maui.Controls` (for window lifecycle events; every MAUI app already
  references it).
- Package `PackageProjectUrl`/`RepositoryUrl` point to `https://github.com/msynk/dotnet-query`.

## 0.8.0 (not published)

A correctness, Blazor and developer-experience overhaul driven by a full review of the library. This version was never
published to nuget.org (its package IDs collided with an unrelated library); its changes ship in 0.9.0.

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

- **Packages consolidated from 16 to 7.** Features that need no extra dependency now ship together;
  namespaces are unchanged, so only package references need updating:

  | Old package | Now in |
  | --- | --- |
  | `DotNetQuery.Core` | `DotNetQuery` (renamed) |
  | `DotNetQuery.DependencyInjection`, `.Http`, `.GraphQL`, `.Persistence`, `.Mvvm`, `.DevTools` | `DotNetQuery` |
  | `DotNetQuery.Persistence.BlazorWasm`, `.DevTools.Blazor` | `DotNetQuery.Blazor` |

  `DotNetQuery` now depends on `Microsoft.Extensions.DependencyInjection.Abstractions` and
  `Microsoft.Extensions.Options`. The IndexedDB script moved to
  `_content/DotNetQuery.Blazor/dotnetquery-indexeddb.js`. `QueryDevTools.PreviewData`, `Preview` and
  `ExportJson` are now annotated `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]` (they always used
  reflection-based JSON). `DotNetQuery.Persistence.Sqlite`, `.Redis`, `.Wpf`, `.WinUI`, `.Avalonia` and
  `.Maui` are unchanged apart from depending on `DotNetQuery`.
- Fetchers run on the thread pool, never on the caller's UI synchronization context.
- `FetchAsync(..., forceRefetch: true)` cancels an in-flight fetch instead of joining it.
- `RefetchQueryAsync` never throws for fetch failures.
- `OfflineMutationQueue.FlushAsync` no longer throws when a replayed mutation fails (see `MutationFailed`).
- `InfiniteQuery.FetchAsync` serves fresh cached pages (and refetches loaded pages when stale) instead of
  always resetting to the first page; `InfiniteQueryOptions` gained `QueryOptions`.
- `QueryDescriptor` gained `Data` and `StaleTime`; `QueryDevToolsStats` gained fetching/cancelled/failed/pending counts.
