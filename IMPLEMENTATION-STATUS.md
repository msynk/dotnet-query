# DotNetQuery - Implementation Status

This document records the **verified** state of the repository: what exists, what is missing, what was
changed, and how each change was verified. It is updated as work lands; the execution plan it tracks is
[IMPROVEMENT-PLAN.md](IMPROVEMENT-PLAN.md). Nothing is listed as done unless it builds, is covered by
tests, and those tests pass.

## Baseline (before this round of work)

Measured on Windows 11, .NET SDK 11.0.100-rc.3 (default) with the 8.0 and 10.0 SDKs installed, commit
`376c4b0`.

| Check | Result |
| --- | --- |
| `dotnet build DotNetQuery.slnx -c Release` | Succeeded, 0 warnings, 0 errors (warnings are errors) |
| `dotnet test DotNetQuery.slnx -c Release` | 245 passed, 4 skipped (Redis tests need `DOTNETQUERY_REDIS`), 0 failed |
| Core test project | 222 passed, 4 skipped |
| Blazor (bUnit) test project | 23 passed |
| NativeAOT | CI job publishes `aot/DotNetQuery.AotHarness` on Linux; locally the ILC analysis runs but the Windows native link fails (missing VC++ linker), see the AOT section below |
| Benchmarks | BenchmarkDotNet suite exists (`tests/DotNetQuery.Benchmarks`); no committed baseline numbers |
| TODO/FIXME/NotImplemented markers | None in `src`, `tests` or `samples` |

### Projects

| Project | Targets | Notes |
| --- | --- | --- |
| `src/DotNetQuery` | net8.0, net10.0 | Engine, DI, persistence (memory/file), offline queues, HTTP/GraphQL fetchers, MVVM, DevTools model, streaming. AOT-compatible, PublicApiAnalyzers on. |
| `src/adapters/DotNetQuery.Blazor` | net8.0, net10.0 | `<Query>`, hook base class, hydration, browser signals, localStorage/IndexedDB persisters, DevTools panel. Not AOT-annotated (Razor). |
| `src/adapters/DotNetQuery.{Wpf,WinUI,Avalonia,Maui}` | per platform | Dispatcher-aware factories over the MVVM bindings. MAUI is outside the solution (workloads). |
| `src/persistence/DotNetQuery.Persistence.{Sqlite,Redis}` | net8.0, net10.0 | Persister providers. |
| `src/DotNetQuery.DevTools.Cli` | net10.0 | Console renderer for DevTools exports. |
| `aot/DotNetQuery.AotHarness` | net10.0 | NativeAOT publish check. |
| `tests/*` | net10.0 | xUnit core tests, bUnit Blazor tests, BenchmarkDotNet. |
| `samples/*` | net10.0 | Blazor TodoApp, Chat, OfflineFirst, ECommerce, Dashboard (console smoke tests in CI), Avalonia (headless CI check), MAUI. |

## Capability inventory at baseline

This table records the state **before** the work log below (commit `376c4b0`). Every Partial and Missing row has since
been resolved; the work log names the item that resolved it, and the latest snapshot is at the end of this file.

Legend: **Done** = implemented and tested; **Partial** = exists with a concrete gap; **Missing** = not implemented.

### Query engine

| Capability | Status | Evidence / gap |
| --- | --- | --- |
| Structural keys, prefix matching, typed definitions | Done | `QueryKey`, `QueryDefinition<T>`; key tests |
| Deduplication, supersession on invalidation, ordered notifications | Done | `QueryEntry<T>` outbox/drainer; `FetchLifecycleTests`, `ConcurrencyRegressionTests` |
| Stale/cache time, GC, refetch interval, focus/reconnect/resume | Done | `QueryEntry<T>`, `QueryCache.CollectGarbage` |
| Retries with back-off | Partial | Count + delay only; **no retry predicate** (a 401/404 is retried like a 503), no `Retry-After` |
| Cancellation with state restore | Done | `CancelQueriesAsync`, cancellation tests |
| `KeepPreviousData` placeholder | Done | `QueryResult<T>.KeepPreviousData` |
| Initial data / initial-data timestamps | Missing | No API |
| Placeholder data (arbitrary) | Missing | Only keep-previous-data |
| Structural sharing | Missing | Listed as P3 in ROADMAP |
| Selectors / fine-grained subscriptions | Missing | Observers always receive every transition of the full state |
| Query filters (status, activity, staleness) | Missing | Only prefix/predicate on the key |
| Bulk reset / remove / refetch by filter | Partial | `RemoveQuery(key)`, `Clear()`, `RefetchQueryAsync(key)` only; no `ResetQueries`, `RemoveQueries(prefix)`, `RefetchQueries(filter)`, `IsFetching` |
| Atomic multi-query updates (transactions) | Missing | `SetQueryData` is atomic per key only |
| Network modes (online / always / offline-first) | Missing | Engine always pauses offline |
| Notification batching | Partial | Ordered per entry; no cross-query batching (Blazor's renderer coalesces renders) |
| Cache snapshots / restore | Partial | Persistence and hydration exist; no in-memory snapshot API |

### Mutations

| Capability | Status | Evidence / gap |
| --- | --- | --- |
| Optimistic updates with exact, conflict-aware rollback | Done | `OptimisticUpdate`, version-checked `TryRestore` |
| Callbacks, retries, invalidation, cancellation | Done | `Mutation<TVariables,TData>`; `MutationBehaviorTests` |
| Mutation cache (inspect/filter running mutations, `IsMutating`) | Missing | Mutations are only visible through diagnostics events / DevTools history |
| Mutation scopes (serial execution) | Missing | Concurrent runs are unordered |
| Multi-key optimistic updates applied atomically | Missing | Each key is cancelled and written separately |
| Overlapping optimistic updates on one key | Partial | A conflicting rollback invalidates (refetches) instead of restoring, which can briefly drop another pending mutation's optimistic value |

### Cache, persistence, offline

| Capability | Status | Evidence / gap |
| --- | --- | --- |
| Persisters: memory, file, SQLite, Redis, localStorage, IndexedDB | Done | Provider tests (Redis skipped locally) |
| `MaxAge`, `Buster`, auto-persist, per-key write ordering | Done | `QueryPersistenceManager` |
| Persistence of dynamic keys (prefix registration) | Missing | Exact registered keys only |
| Encryption hook for persisted data | Missing | |
| In-memory and durable offline mutation queues | Done | `OfflineMutationQueue`, `PersistentMutationQueue` (at-least-once, documented) |
| Queue expiration / dead-letter / idempotency keys | Missing | |
| Cross-tab synchronization | Missing | |
| Cache size limits / eviction | Missing | Only time-based GC |

### Blazor

| Capability | Status | Evidence / gap |
| --- | --- | --- |
| Scoped per-circuit client | Done | `AddDotNetQueryBlazor` |
| `<Query>` with templates, keep-previous-data, dependent queries, prerender hydration | Done | bUnit tests |
| Hook-style `UseQuery`/`UseMutation`/`UseInfiniteQuery` | Done | `DotNetQueryComponentBase` |
| Prerender hydration for hook-style queries | Missing | Only `<Query>` persists/hydrates, so `UseQuery` refetches after prerender |
| Clear cache on sign-out / user switch | Missing | In WebAssembly the scoped client lives for the whole tab, so a second user signing in on the same tab sees the first user's cached data |
| Browser signals (focus, online, visibility) | Done | `BrowserSignalService` |
| Empty / placeholder templates, selectors in components | Missing | |
| Playwright end-to-end tests | Missing | CI has a curl-based prerender smoke test |

### Tooling, packaging

| Capability | Status | Evidence / gap |
| --- | --- | --- |
| DevTools model, Blazor panel, CLI | Done | DevTools tests |
| OpenTelemetry traces/metrics | Done | `DotNetQueryDiagnostics` |
| Public API baselines | Done | PublicApiAnalyzers (Blazor adapter excluded by design) |
| Package metadata | Partial | `PackageProjectUrl`/`RepositoryUrl` point to `github.com/dotnetquery/dotnetquery`, not the real repository |
| Package naming | Done | Package IDs renamed to `Msynk.DotNetQuery.*` (item 27) |

## Defects found during investigation

1. **Package metadata points to the wrong repository** (`Directory.Build.props`). **Fixed** (item 1).
2. **No way to stop retrying non-transient errors** - authentication failures (401/403) and 404s are retried
   `DefaultRetryCount` times with back-off. **Fixed** (item 2).
3. **Previous user's data survives sign-out in Blazor WebAssembly** - nothing clears the scoped client when
   the authenticated user changes. **Fixed** (item 12, opt-in `ResetOnUserChange()`).
4. **Hook-style queries refetch after prerendering** - only `<Query>` uses persistent component state.
   **Fixed** (item 8).
5. **Reconnect refetch is triggered twice in Blazor** - `BrowserSignalService` both updates the
   `ConnectivityProvider` (which the client already treats as a reconnect) and raises `OnlineManager`; with a
   zero stale time and a fast fetch this refetched every active query twice. **Fixed** (item 23).

## Naming collision (resolved in item 27: option 1, owner-scoped package IDs)

NuGet.org (checked 2026-11-02):

| Package ID | Owner |
| --- | --- |
| `DotNetQuery` | free |
| `DotNetQuery.Core` | **taken** - Patrick Sachmann, v2.0.0, "TanStack Query-inspired async data fetching and state management for .NET" |
| `DotNetQuery.Blazor` | **taken** - Patrick Sachmann, v2.0.0, "Blazor components ... for DotNetQuery" |
| `DotNetQuery.Persistence.*`, `.Wpf`, `.Maui` | free |

This repository's `DotNetQuery.Blazor` package therefore **cannot be published** under its current ID, and
the remaining IDs sit next to an unrelated library with the same name and purpose, which confuses search
results and invites prefix-reservation conflicts. Renaming every package ID, namespace and doc reference is
a breaking, outward-facing decision for the project owner, so it has not been done. Options, in order of
preference:

1. **Rename the product and package prefix** (for example an owner-scoped prefix such as `Msynk.Query.*`,
   or a distinct product name) and reserve the prefix on NuGet. Cleanest long-term; requires a migration note.
2. **Keep `DotNetQuery` for the core, rename only the colliding packages** (for example
   `DotNetQuery.Components.Blazor`). Smallest change, but search results stay ambiguous.
3. Coordinate with the other package owner. Out of this repository's control.

## Compatibility constraints

- Target frameworks: net8.0 and net10.0 for portable libraries; analyzers and `TreatWarningsAsErrors` are on.
- Core must stay UI-framework independent, trim- and NativeAOT-safe (no new unannotated reflection).
- Public API changes must be recorded in `PublicAPI.Unshipped.txt` (nothing has shipped yet, so additive
  changes are non-breaking; `IQueryClient` is implemented only by `QueryClient` in this repository, but new
  members are added as extension methods or to `QueryClient` to avoid breaking external implementations).
- NativeAOT: the authoritative check is the CI `aot` job (Linux, clang). Locally on this machine the ILC
  trim/AOT analysis runs and reports IL2xxx/IL3xxx warnings, but the final Windows link fails for lack of the
  VC++ linker.

## Work log

Entries are appended as tasks are verified.

### 1. Package metadata (verified)

- `PackageProjectUrl`/`RepositoryUrl` now point to `https://github.com/msynk/dotnet-query` (matches `origin`).

### 2. Retry classification and `Retry-After` (verified)

- `RetryPredicate` delegate; `QueryOptions.ShouldRetry`, `QueryClientOptions.DefaultShouldRetry`,
  `MutationOptions.ShouldRetry`. Consulted only within the `RetryCount` budget; a throwing predicate means
  "do not retry" and the original fetch error is kept.
- `HttpRetryPolicy`: `IsTransient(Exception|HttpStatusCode)` (network errors, timeouts, 408, 429, 5xx except
  501/505), `ShouldRetry`, `GetRetryAfter`, `RespectRetryAfter(fallback, maxDelay)`.
- HTTP JSON fetchers and the GraphQL fetcher now throw a plain `HttpRequestException` (same type and message as
  `EnsureSuccessStatusCode`) with `StatusCode` set and `Retry-After` recorded in `Exception.Data`.
- Tests: `RetryPredicateTests` (25 cases incl. end-to-end 404-not-retried / 503-retried).

### 3. Query filters and bulk operations (verified)

- `QueryFilter` (key prefix/exact, `Type` active/inactive, `Stale`, `Fetching`, `Status`, `Predicate`),
  `QueryTypeFilter`, `RefetchType`.
- `QueryFilterExtensions`: `InvalidateQueriesAsync(filter, refetchType)`, `RefetchQueriesAsync`,
  `CancelQueriesAsync(filter)`, `ResetQueriesAsync`, `RemoveQueries`, `CountFetching`; `QueryCache.FindAll(filter)`.
- `QueryInvalidatedEvent` is now raised by the cache for every invalidation path (still exactly once per call).
- Tests: `QueryFilterTests` (14).

### 4. Structural sharing (verified, opt-in)

- `QueryOptions.StructuralSharing` / `QueryClientOptions.DefaultStructuralSharing` (default **off**: sharing
  relies on `Equals` meaning full value equality, which identity-based `Equals` overrides violate).
- Reflection-free deep equality (Equals + element-wise lists/arrays/dictionaries); equal refetch keeps the
  cached instance, partial changes reuse unchanged elements in writable collections. Runs outside locks; a
  throwing `Equals` disables sharing for that value instead of failing the fetch.
- Tests: `StructuralSharingTests` (8).

### 5. Selectors and fine-grained notifications (verified)

- `QueryStateProperties` flags; `Watch<T, TResult>(key|definition, select, notifyOn, comparer)` and
  `Watch<T>(key|definition, notifyOn)`. Memoized per data instance, structural comparison of selected values,
  selector errors surface as `Error` while keeping the last good selection, comparer errors cannot break the
  entry's notification drain. Entries now hold an internal `IQuerySubscriber<T>` set.
- Tests: `SelectorTests` (10, incl. 200 concurrent selection observers).

### 6. MVVM change notifications (verified)

- `ObservableQuery<T>` raises `PropertyChanged` only for properties whose value changed (reference identity
  for `Data`), so list controls no longer rebuild on every status flip. Test added to `MvvmTests`.

### 7. Initial and placeholder data (verified)

- `QueryDefinition<T>.InitialData`/`InitialDataUpdatedAt`/`PlaceholderData`, `WithInitialData`,
  `WithPlaceholderData`, `client.SeedInitialData(definition)`, `QueryResult<T>.WithPlaceholderData`.
- Honored by definition-based fetch/prefetch/ensure/watch, selection observers, `ObservableQuery`, Blazor
  `<Query>` and `UseQuery`; `ResetQueriesAsync` restores initial data.
- Tests: `InitialAndPlaceholderDataTests` (12), Blazor `HookHydrationAndPlaceholderTests` (7).

### 8. Hook-style prerender hydration (verified)

- `DotNetQueryComponentBase` now hydrates `UseQuery`/`UseInfiniteQuery` from persisted prerender state,
  persists their data, and makes prerendering wait for the fetches started during a parameter update
  (`SetParametersAsync`). Opt out with `PersistQueryState => false`. Fixes defect 4.

### 9. Atomic multi-query writes (verified)

- `SetQueriesData(tx => tx.Set(...).Update(...))`, `SetQueriesData<T>(filter, updater)`, `QueryDataCommit.Rollback()`.
  Commit protocol: compute every updater outside locks, take all involved entry locks in global id order (the
  only multi-lock site, so no deadlocks), verify versions, apply all or recompute; rollback restores exact
  snapshots atomically and reports conflicting keys.
- Declarative `OptimisticUpdates` now cancel all target keys, then apply in one transaction; rollback is atomic
  and conflicting keys are invalidated (previous per-key behavior preserved for conflicts).
- Tests: `QueryDataTransactionTests` (10, incl. torn-read detection, 400 concurrent writers, opposite-order
  deadlock check). Existing mutation/optimistic tests unchanged and passing.

### 10. Mutation cache and scopes (verified)

- `QueryCache.Mutations` (`MutationCache`): `FindAll(MutationFilter)`, `Find(id)`, `CountPending`, `Changed`,
  `Clear`, `CollectGarbage` (wired into the client GC timer), retention `QueryClientOptions.MutationCacheTime`.
- `MutationOptions.Scope`: serial execution in start order across instances; `MutationState.IsPaused` while
  waiting; optimistic updates/`OnMutate` still apply immediately; scope released after full settlement; a run
  cancelled while waiting rolls back and cannot let later runs overtake.
- Tests: `MutationCacheTests` (10).

### 11. Removal of observed queries (defect fixed, verified)

- New defect found: `RemoveQuery`/`Clear` dropped observed entries, stranding observers (a rendered `<Query>`
  would never update again). Observed entries are now reset in place and refetched; unobserved ones are
  evicted; `QueryClient.Dispose` still disposes everything. Reset detaches the in-flight fetch so an outdated
  result can never land (even if the fetcher ignores cancellation).
- Tests: `RemovalSemanticsTests` (5).

### 12. Blazor authentication isolation (verified)

- `AddDotNetQueryBlazor().ResetOnUserChange(identity?)`, `QueryAuthenticationGuard` (first identity recorded;
  later identity changes remove hidden queries, reset+refetch rendered ones, clear settled mutations; same-user
  re-authentication is ignored; custom identity enables tenant-switch resets; `UserChanged` event for app-owned
  persisted state). Activated by `<Query>`/`DotNetQueryComponentBase`. Fixes defect 3.
- New dependency: `Microsoft.AspNetCore.Components.Authorization` (first-party, same release train).
- Tests: `AuthenticationGuardTests` (4, bUnit test authorization + a custom provider).

### Verification snapshot (after items 1-12)

| Check | Result |
| --- | --- |
| `dotnet build DotNetQuery.slnx -c Release` | 0 warnings, 0 errors |
| `dotnet test DotNetQuery.slnx -c Release` | 349 passed (315 core + 34 Blazor), 4 skipped (Redis), 0 failed |
| Console samples (Chat, OfflineFirst, ECommerce, Dashboard), DevTools CLI, Avalonia headless | all pass |
| Trimmed self-contained publish of the AOT harness (`PublishTrimmed`, win-x64) | 0 IL warnings; all 22 checks pass, including new checks for retry predicates, structural sharing, selectors, filters, transactions, mutation cache/scopes and initial data |
| NativeAOT native link | not runnable locally (VC++ linker missing); CI `aot` job is authoritative |

### 13. Network modes (verified)

- `NetworkMode` (`Online`, `Always`, `OfflineFirst`); `QueryOptions.NetworkMode`, `QueryClientOptions.DefaultNetworkMode`
  (default `Online` = previous behavior), `MutationOptions.NetworkMode`, `DefaultMutationNetworkMode` (default
  `Always` = previous behavior). `Online` mutations wait (pending + `IsPaused`) for connectivity before running
  and before retries; `OfflineFirst` runs the first attempt and pauses retries.
- Tests: `NetworkModeTests` (9).

### 14. Conditional HTTP requests (verified)

- `ConditionalRequestHandler` (`DelegatingHandler`): ETag / Last-Modified revalidation, 304 replay, LRU bound
  (`MaxEntries`), body-size bound, `no-store` respected, pass-through for non-GET and caller validators.
- Tests: `ConditionalRequestHandlerTests` (8, incl. end-to-end refetch with one body transfer).

### 15. Performance baseline (verified, indicative)

- New suites: selectors, structural sharing, transactions, cache footprint. Full suite run with the ShortRun job;
  results committed in `tests/DotNetQuery.Benchmarks/Baselines/2026-11-02-shortrun.md` (cache hit ~140 ns,
  ~22 ns per extra observer, ~1 KB per cached query, sharing ~11 us per 1,000-record compare). ShortRun numbers
  have wide error bars - they are for spotting order-of-magnitude regressions only.

### 16. Browser end-to-end tests (verified)

- `tests/DotNetQuery.E2E` (Playwright 1.64.0, xUnit): launches the built TodoApp in Kestrel and drives the
  installed Chrome/Edge headless. 4 tests: prerender hydration with zero interactive fetches and no loading
  flash (checked through the DevTools panel's fetch count and a MutationObserver), optimistic add confirmed,
  rejected add rolled back, keep-previous-data on filter switch. A negative check (persistence disabled) made the
  hydration test fail with 1 fetch, confirming it detects the regression. Skips when no browser is installed.
- Added to `DotNetQuery.slnx`; runs in the Windows CI job.

### 17. Cross-cache and cross-tab synchronization (verified)

- Core: `DotNetQuery.Synchronization` - `IQuerySyncChannel` (opaque string messages), `InMemoryQuerySyncHub`,
  `QuerySynchronizer` (`Register`/`RegisterPrefix` with `JsonTypeInfo<T>` or reflection, `QuerySyncOptions.ChannelName`,
  `Error`). Data writes and invalidations propagate; last-writer-wins by timestamp with "the current value's sender
  may revise it" (rollbacks propagate); echo suppression by timestamp and a thread-static apply flag; removals are
  deliberately not synchronized (GC). Envelope uses source-generated JSON.
- Blazor: `BroadcastChannelQuerySync` (`dotnetquery-sync.js`), `AddCrossTabSync(register, options, channelName)`,
  `<DotNetQueryCrossTabSync />`. Enabled in the TodoApp sample.
- Tests: `QuerySynchronizerTests` (10); E2E `A_change_made_in_one_tab_appears_in_another_tab` in real Chrome
  (fails when the sync component is removed - negative check done); AOT harness check added.

### Verification snapshot (after items 1-17)

| Check | Result |
| --- | --- |
| `dotnet build DotNetQuery.slnx -c Release` | 0 warnings, 0 errors |
| `dotnet test DotNetQuery.slnx -c Release` | 381 passed (342 core + 34 Blazor + 5 browser E2E), 4 skipped (Redis), 0 failed |
| Trimmed AOT harness publish | 0 IL warnings, 23/23 checks pass |
| TodoApp prerender smoke (CI script) | pass |

### 18. Persistence: dynamic keys and encryption (verified)

- `QueryPersistenceManager.RegisterPrefix<T>(prefix, typeInfo)` (+ reflection overload, `RegisteredPrefixes`):
  persists every cached query of type `T` under the prefix (explicit, `PersistAllAsync`, auto-persist); restore
  applies prefixed records to existing queries and hydrates the rest synchronously when the query is first
  created (before the first fetch checks freshness); `MaxAge` honored; registrations of evicted keys pruned.
- `ProtectedQueryPersister(inner, protect, unprotect)`: payload transformation at rest (encryption hook) for the
  cache and the durable mutation queue; unreadable records are skipped and reported via `Error`.
- Tests: `PersistencePrefixAndProtectionTests` (7).

### 19. Durable offline queue reliability (verified)

- `PersistentMutationQueueOptions`: `MaxAttempts`, `RetryDelay`, `ShouldRetry`, `MaxAge` (expiration with
  `MutationExpiredException`), `UseDeadLetterQueue` (durable `DeadLetters`, restored on startup,
  `RetryDeadLetterAsync`, `DiscardDeadLetterAsync`). Handlers can receive the `PendingMutation` (stable id as an
  idempotency key). `Pause`/`ResumeAsync`/`IsPaused`, `RemoveAsync(id)`, `ClearAsync`. Defaults preserve the
  previous behavior (one attempt, drop on permanent failure).
- At-least-once delivery and the server-side idempotency requirement are documented in `docs/offline-mode.md`.
- Tests: `DurableQueueReliabilityTests` (7); existing queue tests unchanged and passing.

### 20. Blazor templates and DevTools exposure control (verified)

- `<Query Empty="..." IsEmpty="...">`: empty-result template (default: null or empty collection).
- `QueryDevToolsPanel`: `Enabled` (renders nothing and observes nothing when off - production exposure control),
  `ShowData` (hides cached values and mutation variables), and a "(paused)" marker for mutation runs waiting for a
  scope or connectivity (from the mutation cache).
- Tests: bUnit `Disabled_panel_renders_nothing`, `ShowData_off_hides_cached_values`,
  `Query_component_renders_the_empty_template_for_empty_results`.

### 21. Dependency vulnerability scanning (verified)

- `dotnet list package --vulnerable --include-transitive` over the solution found one issue:
  `samples/DotNetQuery.Sample.Avalonia` pulled `Tmds.DBus.Protocol 0.20.0` transitively (GHSA-xrw6-gwf8-vvr9, high).
  Pinned the patched 0.21.3 in the sample; the scan is now clean for every project and the Avalonia headless
  check still passes. Shipped packages were not affected.
- CI: new "Vulnerability scan" step in the Windows job fails the build on any vulnerable package.

### 22. TanStack Query migration guide

- `docs/tanstack-migration.md` maps TanStack concepts, options, state flags, client methods and mutation
  features to DotNetQuery APIs (each mapped API exists in this repository), plus the deliberate differences.

### 23. Duplicate reconnect refetch (defect 5, fixed, verified)

- `QueryClient` coalesces an `OnlineManager` reconnect signal that arrives within one second of the connectivity
  provider reporting the same transition (the Blazor bridge reports both). `OnlineManager.Raised` itself is
  unchanged for app subscribers.
- Test: `A_reconnect_reported_by_both_connectivity_and_the_online_manager_refetches_once` (fails 3/3 runs with
  the coalescing disabled).

### 24. Hierarchical query defaults (verified)

- `QueryClientOptions.SetQueryDefaults(prefix, options)` / `GetQueryDefaults(key)`; layering client defaults ->
  prefix defaults (shortest to longest) -> query options; applied to fetch, prefetch and infinite queries
  (infinite queries keep their own retry defaults unless `QueryOptions` sets them).
- Tests: `QueryDefaultsTests` (3, incl. a reflection guard that `QueryOptions.Merge` layers every property).

### 25. Selectors in Blazor hooks (verified)

- `DotNetQueryComponentBase.UseQuery(definition, select, notifyOn)` returns a selection observer; the component
  re-renders only on changes of the selected value (or chosen properties); prerender persistence/hydration of the
  source query included.
- Test: `UseQuery_with_a_selector_re_renders_only_when_the_selection_changes` (bUnit, render counting).

### 26. Independent review and fixes (verified)

An independent review of the new concurrency-sensitive code found 7 bugs and 2 minor issues; all fixed, each
with a regression test in `ReviewRegressionTests` (9) that exercises the failing path:

| Finding | Fix |
| --- | --- |
| `PersistentMutationQueue.RemoveAsync`/`ClearAsync`/`DiscardDeadLetterAsync` raced with `RestoreAsync` (cleared writes came back and replayed) | Serialized with the enqueue gate; `ClearAsync` also deletes queued/dead entries still in the store |
| `RetryDeadLetterAsync` not atomic; expired letters could never be retried | Store-then-move under the gate; retry restarts the entry's age |
| Synchronizer thread-static echo suppression also hid local changes delivered by the applying thread | Data echoes detected by (sender, timestamp) only; remote invalidations tracked per key |
| Synchronizer last-writer-wins check and write were separate steps | Atomic `QueryEntry.TryApplyRemote` under the entry lock |
| `SetQueriesData<T>(filter)` could run updaters on queries reset after matching | Filter-pinned writes skip entries without data at prepare time |
| Restore-on-first-use could miss a query created during restore; type mismatch consumed the record; dynamic registrations leaked without auto-persist | Subscribe before inspecting the cache; resolve before consuming; a prefix handler prunes on eviction |
| A throwing value-type `Equals` in a selection observer could stop an entry's notifications | Guarded like the comparer |
| (minor) An eviction raced by `GetOrCreate` was not reported | Whoever evicts a removed entry raises `Removed` |
| (minor) Waiters of a fetch detached by a reset got an empty state; queue retry delay could escape `FlushAsync` | Reset links the follow-up refetch as successor and cancels only after starting it; delays guarded and clamped |

Also fixed a pre-existing invariant violation the review pointed out: `SetStateLocked` used record equality on
`QueryState<T>`, which calls the data's `Equals` (user code) under the entry lock; it now compares fields
without calling it.

A test-only race in `MvvmTests.ObservableQuery_raises_only_the_properties_that_changed` (asserting before the
drain thread applied the notifications) was fixed by waiting for the notifications themselves. The core suite
then passed 8 consecutive runs.

### Verification snapshot (after items 1-26)

| Check | Result |
| --- | --- |
| `dotnet build DotNetQuery.slnx -c Release` | 0 warnings, 0 errors |
| `dotnet test DotNetQuery.slnx -c Release` | **412 passed** (369 core + 38 Blazor bUnit + 5 Playwright browser E2E), 4 skipped (Redis needs `DOTNETQUERY_REDIS`), 0 failed; core suite stable over 8 consecutive runs |
| Trimmed self-contained AOT harness publish | 0 IL2xxx/IL3xxx warnings, all 23 checks pass |
| Console samples, DevTools CLI, Avalonia headless, TodoApp prerender smoke | all pass |
| `dotnet list package --vulnerable --include-transitive` | clean for every project |
| NativeAOT native link | not runnable on this machine (no VC++ linker); CI `aot` job is authoritative |
| Benchmarks | ShortRun baseline committed (`tests/DotNetQuery.Benchmarks/Baselines/`) |

### 27. Package naming (resolved)

- Chose the recommended option: owner-scoped package IDs. All packable projects now use `Msynk.DotNetQuery*`
  (`Msynk.` is free on NuGet and the `msynk` account already publishes packages). Namespaces, assembly names and
  the documented Blazor asset path are unchanged (`StaticWebAssetBasePath` pinned to `_content/DotNetQuery.Blazor`),
  so only `PackageReference`s change. E2E tests confirm the JS modules and scoped CSS still load.

### 28. Fetch scheduling, memory bounds, snapshots, batching, dependencies (verified)

- `QueryOptions.Timeout` (per attempt, `TimeoutException`, abandons token-ignoring fetchers), `Priority`,
  `AdaptiveRefetchInterval`; `QueryClientOptions.MaxConcurrentFetches` (priority queue, cancellation-aware),
  `FetchRateLimiter` (`IFetchRateLimiter`, no new dependency; `QueryRateLimitedException`), `Interceptors`
  (`IQueryInterceptor`), `MaxInactiveQueries` + `Cache.Trim`, `TrimOnMemoryPressure` (gen-2 GC callback),
  `AddInvalidationDependency` (transitive, cycle-safe, applies even when nothing under the requested key is cached).
- `Cache.CreateSnapshot(filter)` / `QueryCacheSnapshot.Restore(removeOthers)` (detaches in-flight fetches);
  `QueryNotifications.Batch()` (AsyncLocal, nestable); `FetchAllAsync(definitions)`.
- Tests: `EngineSchedulingTests` (13; limiter test stable over 5 runs).

### 29. Overlapping optimistic updates (verified)

- Optimistic writes form per-entry layers (`QueryEntry.Optimistic.cs`). Rolling back a layer with layers above
  rebases them on its snapshot (updaters outside locks, epoch/version verified); top layers restore exactly;
  accepted layers stay re-appliable while pending layers sit below them; foreign writes make the stack stale and
  fall back to invalidate.
- Tests: `OptimisticLayerTests` (6, incl. 30 overlapping mutations settled in random order converging with zero
  refetches).

### 30. Mutation defaults, deduplication, progress (verified)

- `QueryClientOptions.SetMutationDefaults(defaults)` / `SetMutationDefaults(prefix, defaults)` / `GetMutationDefaults`
  (`MutationDefaults`: retries, delay, predicate, network mode, scope, invalidations); `MutationOptions.DeduplicatePending`;
  progress-reporting `CreateMutation((vars, progress, ct) => ...)` with `MutationState.Progress` and
  `MutationDescriptor.Progress`.
- Tests: `MutationExtrasTests` (3).

### 31. HTTP resilience, token refresh, request batching (verified)

- `CircuitBreakerHandler` (+ `CircuitBreakerOptions`, `CircuitState`, `BrokenCircuitException` with retry-after),
  `BearerTokenHandler` (single-flight refresh on 401, body replay), `BatchLoader<TKey, TValue>` (DataLoader pattern,
  `BatchLoaderOptions.Delay`/`MaxBatchSize`, shared failures, `KeyNotFoundException` for missing keys).
- Tests: `HttpResilienceTests` (6).

### 32. Persistence migrations, replay policy, crash replay, diagnostics (verified)

- `QueryPersistenceOptions.Migrations` (old buster to payload rewrite or drop; re-saved under the current buster).
- `PersistentMutationQueueOptions.OnReplayFailure` with `ReplayDecision` (`Default`, `Retry`, `Fail`, `Drop`, `Pause`);
  `HttpRetryPolicy.IsConflict` (409/412).
- Crash-between-delivery-and-removal test proves at-least-once replay with a stable idempotency key.
- Diagnostics: `QueryRetryEvent`, `OptimisticUpdateAppliedEvent`, `OptimisticUpdateRolledBackEvent`;
  `QueryClientOptions.IncludeVariablesInDiagnostics`, `DiagnosticKeyFormatter` (OpenTelemetry tags).
- Tests: `MigrationReplayDiagnosticsTests` (5).

### 33. DevTools: timelines, profiles, hierarchy, time travel, queue inspection (verified)

- Model: `Timeline(key)`, `ProfileQueries()` (`QueryProfile`), `KeyTree()` (`QueryKeyNode`), `TakeSnapshot`/
  `Snapshots`/`RestoreSnapshot` (`DevToolsSnapshot`), `ResetAsync`, `AttachQueue`/`QueuedMutations`/`DeadLetters`;
  `MutationRecord.OptimisticKeys`/`RolledBackKeys`/`RefetchedKeys`; export includes profiles; CLI renders
  "Slowest queries".
- Panel: Snapshots tab, Offline queue tab (`Queue` parameter), Tree toggle, status filter, timeline/profile/Reset in the
  details pane, optimistic column in Mutations.
- Tests: `DevToolsAdvancedTests` (6), bUnit panel tests (+3).

### 34. Pagination helpers and Blazor composition (verified)

- Core: `InfiniteQuery.FetchPagesAsync(count)`, `PrefetchInfiniteQueryAsync(..., pages)`, `InfiniteData.Items(...)`,
  `InfiniteQueryState.PageCount`.
- Blazor: `InfiniteQuery.ToItemsProvider(items, totalCount)` for `<Virtualize>`, `<ScrollRestoration>` (+
  `dotnetquery-scroll.js`), `<QueryClientProvider>` (cascading client honored by `<Query>`, hooks and DevTools),
  `<Query NotifyOn>`, `UseQueries(definitions)`, `<DotNetQueryNavigation>` (cancel fetches left behind, optional
  stale refetch).
- Tests: `InfinitePaginationTests` (2), `VirtualizeAndScrollTests` (3), `ProviderAndNavigationTests` (4).

### 35. Blazor render modes, sample pages and browser tests (verified)

- The TodoApp has per-page render modes (no global one): Interactive Server (`/`, `/todos/{id}`, `/large`, `/grid`,
  `/tenants`), Interactive WebAssembly (`/wasm`), Interactive Auto (`/auto`) and static streaming SSR (`/ssr`). The
  new `samples/TodoApp.Client` project holds the browser components and an `ITodoClient` abstraction (HTTP in the
  browser, the data layer while prerendering); it is in the solution and CI builds it.
- Pages: `EditForm` editing through an optimistic mutation, a 10,000-row infinite query behind `<Virtualize>` with
  `<ScrollRestoration>`, `QuickGrid` paging where each page is a query, tenant-scoped keys with removal on switch.
- Defect found by the tenant test (sample): removing the previous tenant's queries in the same handler that changed
  the key removed them while still observed, so the observer refetched them. The handler now renders first, then
  removes.
- `QueryCircuitHandler` (sample + `docs/blazor.md`) refetches stale active queries when a dropped circuit
  reconnects; `CircuitHandler` is server-only, so it is a documented pattern, not part of the WebAssembly-safe
  adapter.
- Tests: `TodoAppPagesTests` (7: WebAssembly hydration without API calls, Auto, edit, virtualized list, grid,
  tenants, streaming SSR) + `TodoAppTests` (5) - 12/12 pass. CI smoke checks the server and WebAssembly prerender
  state and the API.
- Flaky test fixed: `Concurrency_limit_caps_running_attempts_and_admits_higher_priority_first` released the slot
  before both waiters had reached the limiter on a loaded machine; it now waits for both to be fetching first
  (3 consecutive full-suite runs green).

### 36. Desktop and mobile: lifecycle, connectivity, MVVM commands, infinite bindings (verified)

- Core: `AppLifecycleSignals` (focus only after a real deactivation, resume suppresses the activation that follows,
  network availability to `ConnectivityProvider`; registered by `AddDotNetQuery`), `NetworkConnectivityMonitor`
  (`NetworkChange`, not supported in the browser).
- MVVM: `ObservableInfiniteQuery<TParam, TPage>` (change-only notifications, `FetchNextPageCommand` /
  `FetchPreviousPageCommand` / `RefetchCommand`), `ObservableQuery.RefetchCommand`, `ObservableMutation.MutateCommand`
  (disabled while pending, validates the parameter type) and `ResetCommand`; command failures are observed, never
  thrown on the UI thread.
- Adapters: `WpfQueryLifecycle` (Application activation, `SystemEvents.PowerModeChanged` resume, network),
  `WinUIQueryLifecycle` (Window activation, network), `AvaloniaQueryLifecycle` (window activation on desktop,
  `IActivatableLifetime` on mobile/macOS, network), `MauiQueryLifecycle` (window activation/resume/stop +
  `MauiConnectivityBridge`), `MauiQueryPersistence.CreateFileSystemPersister()`; every adapter gained
  `CreateObservableInfiniteQuery`. The MAUI adapter now references `Microsoft.Maui.Controls` (window events).
- Samples: Avalonia binds its button to `RefetchCommand` and attaches `AvaloniaQueryLifecycle`; the headless CI
  verify now also checks the command refetch and the focus refetch after re-activating the window (PASS). MAUI
  sample uses `MauiQueryLifecycle` + `RefetchCommand`; it and the adapter build for Android locally (0 warnings).
- Docs: `docs/desktop-and-mobile.md`, adapter READMEs.
- Tests: `LifecycleAndMvvmTests` (11). WPF/WinUI lifecycle classes are compile-verified only (their events need a
  real desktop session); the logic they forward to is unit-tested.

### 37. GraphQL mutations and error details (verified)

- `HttpClient.GraphQLMutationFn<TVariables, TData>(uri, mutation, variablesTypeInfo, dataTypeInfo)` (trim/AOT-safe;
  typed variables object serialized as the GraphQL `variables`), a reflection overload, and
  `IQueryClient.CreateGraphQLMutation(...)` (a regular `Mutation`, so optimistic updates/invalidation/offline apply).
- `GraphQLError.Path` and `.Code` (`extensions.code`), `GraphQLException.HasCode(code)` for retry decisions.
- Tests: `GraphQLQueryExtensionsTests` (+4, source-generated JSON context); AOT harness check.

### 38. Source generator: queries and mutations from API interfaces (verified)

- New `src/DotNetQuery.SourceGenerators` (incremental Roslyn generator, netstandard2.0, Roslyn 4.8 so it runs on the
  .NET 8 SDK); packed into `Msynk.DotNetQuery` under `analyzers/dotnet/cs` - no extra package.
- `[GenerateQueries]` on an interface or `[assembly: GenerateQueries(typeof(IApi))]` for interfaces that cannot be
  edited (OpenAPI-generated, other assemblies) generates `<Name>Queries`: hierarchical key factories
  (`Keys.X(args)`, `Keys.XPrefix`, `All`), `QueryDefinition<T>` factories, and per mutation `XFn`, `XOptions`
  (mutation key + invalidation) and `X(client, api)`; multi-parameter mutations get a variables record.
- Classification: `[QueryMethod]`/`[MutationMethod]`/`[QueryIgnore]`, then Refit verb attributes, then name
  prefixes. Handles `ValueTask`, result-less mutations, default values (enum members rendered by name), keyword
  parameter names and collisions with generated parameter names, and NSwag-style token/no-token overloads (one member,
  token wins). `Invalidates` resolves method names; diagnostics DNQ001 (unsupported annotated method), DNQ002 (unknown
  invalidation target), DNQ003 (not an accessible non-generic interface).
- OpenAPI pipeline: OpenAPI -> Refitter (Refit interface) or NSwag (client interface) -> assembly attribute -> queries.
  Documented in `docs/source-generation.md`.
- Tests: `tests/DotNetQuery.SourceGenerators.Tests` (6: output compiles with zero warnings in a nullable, documented,
  warnings-as-errors consumer; assembly-level naming; DNQ001/2/3; incremental caching on unrelated edits) and
  `GeneratedQueriesTests` (8, runtime behaviour through the real generator: keys, fetch/caching, options, targeted and
  default invalidation, variables records, result-less and ValueTask mutations, assembly-level NSwag shape).
- Trim/AOT: the AOT harness now uses a generated API; trimmed self-contained publish has 0 IL warnings and all 29
  checks pass. The generator project ignores app publish properties (`TreatAsLocalProperty`), so
  `dotnet publish -r <rid> -p:PublishTrimmed=true` of a referencing app works.

### 39. Reliability stress tests (verified)

- `ReliabilityStressTests` (8): 300 observers converging under 50 concurrent invalidations/refetches; cancelling
  during a 30 s retry back-off (no further attempt, previous data restored, x20); observers disposing themselves and
  each other inside notifications (x50, no observer errors); disposing the client mid-fetch (x30); evicting
  (`MaxInactiveQueries`, `Trim`, `CollectGarbage`) while 200 fetches run; hydration racing fetches (newer data always
  wins, x50); connectivity flapping every millisecond during 40 `NetworkMode.Online` mutations (each runs exactly
  once); shutting down during auto-persist leaves a store that restores cleanly (x10). Stable over 5 consecutive runs.
- Pack validation: all 7 packages carry license, icon, README, SourceLink repository/commit and `.snupkg` symbols;
  `Msynk.DotNetQuery` contains `analyzers/dotnet/cs/DotNetQuery.SourceGenerators.dll` and depends only on
  `Microsoft.Extensions.DependencyInjection.Abstractions` and `Microsoft.Extensions.Options`. A fresh net8.0 console
  app restoring the packed `.nupkg` (warnings as errors, XML docs on) used `[GenerateQueries]` and ran correctly.

### 40. Performance: hot paths, new benchmarks, allocation budgets, net8.0 test runs (verified)

- Hot paths: `QueryCache.GetOrCreate` gained an allocation-free lookup (the closure only on creation);
  `FetchAsync` passes its arguments as a struct to a static lambda (no closure per call); entries reuse completed
  `Task<QueryState<T>>` / `Task<QueryResult<T>>` while the state is unchanged; attempts call the fetcher directly
  when no limiter, rate limiter or timeout is configured. Measured: fresh cache hit 488 -> 32 B/op (allocation test)
  and 432 -> 104 B (benchmark); `SetQueryData` with an observer 216 -> 120 B/op; `ForcedRefetch` 1,336 -> 1,224 B.
- New benchmark suites: `LargeUpdateBenchmarks`, `InfiniteQueryMemoryBenchmarks`, `PersistenceBenchmarks`,
  `EvictionBenchmarks`, `CacheComparisonBenchmarks` (ConcurrentDictionary / MemoryCache / DotNetQuery) and
  `BlazorRenderBenchmarks`; baseline `Baselines/2026-11-03-shortrun.md` (ShortRun, indicative).
- Regression detection: `AllocationBudgetTests` (4; budgets 2-3x the measured cost, serial collection) run in every
  test pass on both runtimes.
- `DotNetQuery.Tests` and `DotNetQuery.Blazor.Tests` now target net8.0 and net10.0 (two net9-only usages in tests
  replaced); both runtimes pass.
- Structural sharing now applies to `SetQueryData` (values and updaters), hydration and synchronized data, not only
  fetches (query option once fetched, client default before); equal writes no longer notify data-only observers.
  Tests: `StructuralSharingTests` (+4).

### 41. WebAssembly offline, documentation set and site (verified)

- WebAssembly offline: the shared `ClientTodos` component renders `<DotNetQueryBrowserSignals />`, shows connectivity,
  and adds todos with `NetworkMode.Online`. New Playwright test: going offline keeps the list, a todo added offline
  is shown optimistically and not sent; back online it is sent once and confirmed. `docs/offline-mode.md` gained a
  WebAssembly/PWA section (IndexedDB persistence, durable queue, service-worker guidance).
- New guides: `architecture.md` (pieces, fetch lifecycle, outbox, mutations, GC, persistence, threading),
  `troubleshooting.md`, `tutorials.md` (8 scenario walkthroughs mapped to samples), `migration.md` (package IDs,
  behavior changes, new dependencies), `releasing.md` (SemVer, breaking-change rules, deprecation, release
  checklist), `desktop-and-mobile.md`, `source-generation.md`; Blazor guide gained render modes, reconnection, forms
  and grids, SignalR.
- Documentation site: `docs/docfx.json`, `docs/toc.yml`, `docs/index.md` and `.github/workflows/docs.yml` (builds on
  pull requests, publishes to GitHub Pages from `main`; enabling Pages is a one-time owner setting).

### Verification snapshot (after items 28-41, 2026-11-03)

| Check | Result |
| --- | --- |
| `dotnet build DotNetQuery.slnx -c Release` | 0 warnings, 0 errors (two blocking test waits flagged by xUnit1031 made async) |
| `dotnet test DotNetQuery.slnx -c Release` | **1,013 passed**, 8 skipped (Redis, needs `DOTNETQUERY_REDIS`), 0 failed: core 449 on net8.0 + 449 on net10.0, Blazor bUnit 48 + 48, source generator 6, Playwright browser E2E 13 |
| Trimmed self-contained AOT harness publish | 0 IL2xxx/IL3xxx warnings, all 32 checks pass (incl. generated queries, GraphQL mutation, MVVM infinite query, lifecycle signals) |
| Console samples (Chat, OfflineFirst, ECommerce, Dashboard), DevTools CLI | all exit 0 |
| Avalonia headless verify | PASS (render, command refetch, focus refetch through `AvaloniaQueryLifecycle`) |
| TodoApp CI smoke | PASS (server + WebAssembly prerender state, API) |
| MAUI adapter + sample (net10.0-android) | 0 warnings, 0 errors |
| `dotnet list package --vulnerable --include-transitive` | clean for every project, including MAUI |
| `dotnet pack` | 7 packages + symbols; metadata, README, icon, SourceLink and the bundled generator verified; packed generator smoke-tested in a fresh net8.0 consumer |
| DocFX site (`docfx docs/docfx.json`) | builds, 0 warnings in the build step, 0 errors |
| Benchmarks | `Baselines/2026-11-03-shortrun.md` (ShortRun, indicative) |

### 42. Documentation review: stale plans and gaps (2026-11-04)

A review of every Markdown document found stale entries (ROADMAP listed the shipped source generator as remaining work;
README/ROADMAP still said 412 tests and 10 guides; this file's capability inventory and "Open items" were out of date;
the TanStack guide mapped `QueryClientProvider` to DI only; two tutorials pointed at samples that did not show what
they described) and real gaps, all addressed by items 43-55. ROADMAP, README, the guides and this file were updated.

### 43. Mutation retry opt-out and `Meta` (verified)

- `MutationOptions.RetryCount` and `InfiniteQueryOptions.RetryCount` are `int?`: `null` uses `MutationDefaults`, `0`
  opts out (previously 0 meant "unset", so a mutation could not opt out of default retries).
- `QueryOptions.Meta`, `MutationOptions.Meta`, `MutationDefaults.Meta`, layered like the other options, surfaced on
  `QueryDescriptor`, `MutationDescriptor` and the completed/failed/started events. The `QueryOptions.Merge` reflection
  guard covers it.
- Tests: `MetaAndRetryOptOutTests` (4).

### 44. Durable queue shared by several instances (verified)

- `PersistentMutationQueueOptions.ReplayLock` (`IMutationQueueLock`): a flush acquires the lock, reconciles the pending
  list and dead letters with the store, then replays. `RestoreAsync` reconciles too (removed entries disappear).
  Queue ids gained a per-instance suffix (two instances in the same millisecond collided before).
- `FileMutationQueueLock` (exclusive lock file, polling, cancellable); Blazor `WebLocksMutationQueueLock` over
  `navigator.locks` (`dotnetquery-locks.js`; cancellable even if the JS runtime ignores the token; no-op where the API is
  missing).
- Tests: `SharedQueueLockTests` (4: two queues over one store replay three writes exactly once and in order under
  concurrent reconnects; ids never collide; restore drops entries another instance removed; the file lock admits one
  holder and honors cancellation), bUnit `WebLocksMutationQueueLockTests` (3), AOT harness check.

### 45. Logging and live DevTools (verified)

- `AddDiagnosticLogging(QueryDiagnosticsLoggingOptions)` / `client.LogDiagnostics(logger)`: source-generated
  `LoggerMessage`s (event ids 1000-1006, 1100-1104), levels per kind, keys through the configured formatter, no
  variables or data. New dependency: `Microsoft.Extensions.Logging.Abstractions`.
- `QueryDevToolsServer`: loopback-only HTTP endpoint for the DevTools export (TcpListener, port 0 or fixed, optional
  bearer/query token compared in constant time, 404/405/401 handling). CLI: `watch <url>`, `<url>`, `watch demo`.
- Tests: `DiagnosticsLoggingTests` (2), `DevToolsServerTests` (3); CLI `watch demo` run; AOT harness logging check.

### 46. Redis synchronization channel (verified against Redis 7)

- `RedisQuerySyncChannel` (`ConnectAsync`/`CreateAsync`, ordered message queue, owned or shared multiplexer).
- Tests: `RedisSyncChannelTests` (2: ordered delivery of 20 messages; two clients synchronized through Redis) - run
  against a local `redis:7-alpine` container and in the Linux CI job.

### 47. `Msynk.DotNetQuery.Blazor.Server` (verified)

- `QueryCircuitHandler` + `RefetchOnCircuitReconnect(QueryCircuitHandlerOptions?)` (filter, invalidate); the TodoApp
  uses it instead of its own copy. Tests: `CircuitReconnectTests` (2).

### 48. Source generator: classes, generic methods, infinite queries (verified)

- Targets may be non-static, non-generic classes (public instance methods up the base chain, `Dispose`/`DisposeAsync`
  and `object` members skipped) - the Kiota wrapper pattern.
- Generic methods generate generic members with their constraints; each type argument is a key segment
  (`QueryKey.Create<T>()[0]`); multi-parameter generic mutations get generic variables records.
- `[InfiniteQueryMethod("page")]` generates the key without the page parameter, `XPageFn` and an `InfiniteQuery`
  factory; invalidation targets resolve to it. DNQ001 reports a missing page parameter.
- Tests: generator tests (+3, 9 total, zero-warning consumer compiles), `GeneratedQueriesExtrasTests` (3 runtime),
  AOT harness check.

### 49. Blazor adapter: public API baseline and trimming (verified)

- PublicApiAnalyzers enabled (174 members recorded; `RS0041` suppressed for Razor-generated `BuildRenderTree`).
- `IsAotCompatible` on: prerender hydration now writes a hand-built envelope as a JSON string (strings need no
  reflection metadata), `UseHydrationJson(IJsonTypeInfoResolver)` and `JsonTypeInfo<T>` overloads make it trim-safe;
  reflection is the documented fallback; DevTools previews are suppressed as development-only.
- Trimmed publish of `samples/TodoApp.Client` (`SuppressTrimAnalysisWarnings=false`): no trim warning from a DotNetQuery
  assembly (only ASP.NET Core/JSInterop framework warnings) - now a CI gate in the `aot` job.
- Tests: hydration tests updated to the envelope, `HydrationJsonTests` (2); the Playwright WebAssembly hydration tests
  pass unchanged.

### 50. Platforms: WPF/WinUI on .NET 8, lifecycle tests, WPF sample, Uno adapter (verified)

- WPF and WinUI adapters multi-target `net8.0-windows*` and `net10.0-windows*`.
- `tests/DotNetQuery.Desktop.Tests`: `WpfQueryLifecycle` with a real `System.Windows.Application` on an STA thread
  (activation after deactivation refetches, system resume refetches, disposal stops forwarding, network monitor) and the
  WinUI activation mapping (4 tests).
- `samples/DotNetQuery.Sample.Wpf` with `verify` (window renders `fetch #1`, bound `RefetchCommand` loads `#2`,
  deactivate/activate loads `#3`): PASS; a CI step.
- `Msynk.DotNetQuery.Uno` (`net9.0`/`net10.0`, Uno.WinUI 6.7, generators stripped): factories and `UnoQueryLifecycle`
  (window activation, background/foreground, network except in the browser). Uno's `net10.0` assemblies are reference
  implementations, so `tests/DotNetQuery.Uno.Tests` covers the activation mapping only.

### 51. Samples match the tutorials (verified)

- OfflineFirst: durable `PersistentMutationQueue` across a restart, idempotency keys, retry classification and a
  dead-lettered 409 through `OnReplayFailure`.
- ECommerce: declarative `OptimisticUpdates` over two keys, three concurrent adds with one rejected and rolled back
  alone, one reconciliation after the burst (with per-add `Invalidates`, a refetch landed while other adds were pending
  and briefly showed the server's cart - documented in the optimistic updates guide, backlog item in ROADMAP).
- The samples reference the patched ASP.NET Core 10.0.14 packages (GHSA-g27p-qf6j-c96c reported for 10.0.13).

### 52. CI, release and repository hygiene

- CI: coverage (`coverage.runsettings`, ReportGenerator summary and artifact), WPF sample and DevTools `watch demo` steps,
  trimmed WebAssembly gate, `test-latest-runtime` (core suite on the newest .NET, rolled forward; non-blocking while it
  is a preview - passes locally on .NET 11 RC 3), `-ci.N` package suffix, symbols uploaded.
- `release.yml`: tag must match `VersionPrefix`, unshipped API check, build/test/pack (MAUI included), provenance
  attestation, nuget.org trusted publishing, GitHub release with the changelog section.
- `eng/prepare-release.ps1` (verified on a copy: ships 1,905 API entries across 10 packages and the 3 analyzer rules,
  sets the version, dates the changelog; `-DryRun`).
- `EnablePackageValidation` for every package (pack passes), `SECURITY.md`, `CODE_OF_CONDUCT.md`, issue and PR templates,
  Dependabot (major updates of per-TFM Microsoft packages ignored).
- `VersionPrefix` 0.9.0: 0.8.0 was never published, 0.9.0 is the first public release (changelog and migration guide
  updated).
- Flaky test fixed: `Batch_loader_coalesces_many_queries_into_one_request` relied on five fetchers (each started on its
  own thread-pool thread) reaching the loader within one `Task.Yield`; under coverage instrumentation one missed the
  burst on net8.0. It now uses an explicit batching window (`BatchLoaderOptions.Delay`); stable over 3 more runs.
- `AllocationBudgetTests.A_fresh_cache_hit_stays_on_the_fast_path` made async (the last xUnit1031 warning).

### 53. Documentation site

- DocFX reads the API reference from built assemblies (`artifacts/docs-bin`), so `.razor` components (`Query<T>`,
  `QueryClientProvider`, `QueryDevToolsPanel`, `ScrollRestoration`, ...) are documented; 0 warnings; the docs workflow
  builds the assemblies first and treats warnings as errors.

### 54. Refreshes wait for pending optimistic updates (verified)

The product issue found in item 51: when several optimistic mutations of one list were in flight, the first to settle
refetched (its `Invalidates`) and the result - server data without the still-pending items - replaced them on screen
until the others settled.

- `QueryEntry`: while an uncommitted optimistic layer is on the entry (the stack still describes the data), refresh
  fetches (invalidation, `RefetchQueryAsync`/`RefetchQueriesAsync`, intervals, focus/reconnect/resume triggers, stale
  reads) are deferred: the entry is marked invalidated, an outdated in-flight fetch is cancelled, and the current state
  is returned. `QueryDataCommit.Settled()` (called by a mutation once fully settled, after its callbacks and
  invalidation) runs one refetch when no pending layer is left and none started meanwhile; inactive entries stay stale.
  Infinite next/previous page loads pass `deferWhileOptimistic: false`.
- A deferred invalidation completes at once (not awaited), so serial `Scope`s whose later run already applied its
  optimistic write cannot deadlock.
- `TryRebase` re-plans when only the stack's bookkeeping changed (a layer below committed and was trimmed) and drops the
  stack when a rebase cannot complete, so conflict refetches are never deferred behind layers that no longer match.
- Tests: `OptimisticRefetchDeferralTests` (5: three concurrent adds with `Invalidates`, one rejected - no state without
  the pending items, exactly one refetch; invalidate/refetch/stale read during a pending write run once after it
  settles; inactive queries stay stale; scoped mutations do not deadlock; infinite next page not deferred). Negative
  check: with the deferral disabled, the three behavior tests fail. The ECommerce sample went back to `Invalidates` on
  each add (12/12 runs pass, one refetch per burst).
- Found while stress-running the suite: `DeduplicatePending` removed a run from its pending list in a continuation that
  could run after the caller's own continuation, so a call made right after a run completed joined the settled run
  (`Deduplicated_mutations_join_a_pending_run_with_equal_variables` failed about once in 6 full runs). The run now leaves
  the list in a `finally` before its task completes; 10 consecutive full core runs are clean.

### 55. Mutations settling together refetch once (verified)

Left open by item 54: when two mutations of one query settled at the same moment, both invalidated it; the second
invalidation cancelled the refetch the first had just started and started another (two fetches, one cancelled).
Invalidation restarts an in-flight fetch because it may predate the change - but a mutation knows when its change
happened.

- `QueryRuntime.NextSequence()`: a logical clock shared by fetches and mutations (timestamps cannot order events under a
  fake or coarse clock). Each fetch cycle records its start (`StartSequence`) and whether it replaces the data
  (`IsRefresh`: not an infinite query's next/previous page); a mutation takes a sequence right after its server call
  settles.
- The mutation's `Invalidates` go through `QueryClient.InvalidateQueriesAfterAsync(key, changeSequence)`: an entry whose
  in-flight refresh started after the change is not marked stale and the refetch joins that refresh (if it does not
  complete, the entry is marked stale after all - `CoversInvalidation`); an entry whose last successful refresh started
  after the change needs no refetch. A fetch that started before the change is still cancelled and restarted. Plain
  `InvalidateQueriesAsync` (no known change time) is unchanged.
- Tests: `InvalidationJoinTests` (5: two mutations whose responses arrive together refetch once with no cancellation,
  50 rounds; a refresh in flight before the server call is still restarted and the outdated result never lands; plain
  invalidation still restarts; a refresh completed after the server call covers the invalidation; an infinite query's
  next-page fetch does not). Negative check: with mutations routed through plain invalidation, the two behavior tests
  fail. When the second response only arrives after the first mutation's refetch started, that refetch may predate the
  second write, so it is still restarted - correctly (this is why the ECommerce sample keeps distinct server latencies).

### 56. Real-time cache synchronization (SignalR), 2026-11-04

Plan: the real-time cache synchronization plan (typed SignalR event mappings, deduplication and ordering, reconnect
recovery, race safety with fetches and optimistic mutations, cross-tab relay, diagnostics, tests, samples, docs).

**Found at the start.** `StreamIntoCacheAsync` (an `IAsyncEnumerable` written into one key), `QuerySynchronizer`
(data and invalidation replication between caches over `IQuerySyncChannel`, with BroadcastChannel and Redis transports)
and a SignalR *channel* recipe in the docs. Missing: event-to-cache mappings, event identity/ordering, recovery after a
reconnect, coordination with in-flight fetches, a SignalR package, diagnostics for any of it.

**Design decisions.**
- The engine is in the core (`DotNetQuery.Synchronization`): `QueryEventSynchronizer`, `QueryEventMapping<TEvent>`,
  `QueryEntity<TItem, TId>`, `QueryEventMetadata`/`QueryEventEnvelope<T>`, `QuerySyncRecovery`, `IQueryEventSource` +
  `InMemoryQueryEventSource`, `QueryEventRelay`. No SignalR, browser or UI dependency (the core's package references
  are unchanged). Transports are separate packages: `Msynk.DotNetQuery.SignalR` (`HubConnectionQueryEventSource`,
  `AddSignalRSync`).
- Reused, not duplicated: writes go through the existing atomic transaction (`QueryDataTransaction`, one per mapping,
  with a new internal `SkipUnchanged` so unchanged queries are not re-rendered); invalidations through
  `InvalidateQueriesAsync`; fetch supersession through `RefetchAsync(CancelAndRefetch, joinRefreshStartedAfter)` and the
  logical clock of item 55; optimistic conflicts through the existing layer rollback (a foreign write makes the rollback
  refetch instead of restoring); cross-tab transport through `IQuerySyncChannel`/`BroadcastChannelQuerySync`.
- One bounded channel drained by one loop: events apply in arrival order and the transport callback only enqueues.
  Overflow drops and schedules a recovery (or waits, opt-in). Invalidations are coalesced per burst.
- Every recovery strategy falls back to invalidation; mappings whose targets depend on the event must declare `Covers`
  (or `NoRecovery()`), so a recovery cannot miss a query. Replay is only offered as an explicit server protocol.
- Cross-tab notices carry no payload, data or keys (only event name/id, scope, schema version); a receiver invalidates
  what its own mappings cover.

**Fixed during self-review.** `ChangeScope` cleared the sequence checkpoints immediately, so an event of the old scope
still being processed could re-seed them and make the new scope's first events stale (now cleared on the loop); an
event discarded as stale by its entity version did not consume its sequence position, so a contiguous stream reported a
false gap (now advanced). Both have regression tests.

**Tests.** Core: `QueryEventSynchronizerTests` (44: every operation and shape, atomicity, failure isolation, handlers,
validation, concurrent duplicates, window expiry and capacity, required ids, malformed metadata, version and sequence
ordering, gaps, replay, replay without history, throwing recovery, snapshot watermark, scopes and scope change, local
echoes, a fetch predating an event, a first fetch in flight, an event during a pending optimistic update surviving the
rollback, a late mutation response, burst coalescing, overflow, disposal under concurrent delivery, self-disposal from a
handler, subscription lifetime, reconnect recovery coalescing, permanent failure, first-connect recovery, envelopes,
diagnostic events), `QueryEventRelayTests` (6), `QuerySyncDiagnosticsTests` (2: logging without payloads, DevTools).
SignalR (`tests/DotNetQuery.SignalR.Tests`, in-process TestServer hub with a switchable network): typed delivery,
redelivery, reconnect with group rejoin and missed-event recovery, replay through a hub method, three reconnect cycles
with one subscription, failed start with and without a restart policy, a rejected resubscription, a connection closed for
good, ownership, disposal during a recovery, a 2,000-event ordered burst, DI (12, net8.0 and net10.0). Blazor:
`QuerySyncComponentTests` (3) and a DevTools "Sync" tab test. E2E: a todo added through the API appears in an open
WebAssembly tab through SignalR. Sync suites stable over repeated runs (core 8x, SignalR 6x, both target frameworks).

**Other evidence.** `samples/DotNetQuery.Sample.Realtime` exits 0 (3 runs; CI smoke test added). Trimmed self-contained
AOT harness publish with the SignalR package: 0 IL warnings, all checks pass including the two new ones (NativeAOT link:
CI only, as before). Trimmed `TodoApp.Client` publish (now with the SignalR client): 0 trim warnings from DotNetQuery
assemblies. BenchmarkDotNet `EventSyncBenchmarks` ShortRun baseline committed
(`tests/DotNetQuery.Benchmarks/Baselines/2026-11-04-shortrun-eventsync.md`).

**Not done / limits.** No server-side event store (replay needs the application's own history endpoint); deduplication
and checkpoints are in memory; prefix-matching mappings scan the cache per event (no index); the cross-tab relay does not
elect a leader connection (every tab keeps its own); the Blazor panel's Sync tab is read-only.

## Verification snapshot (after item 56, 2026-11-04)

Measured on this machine without a Redis server; checks not re-run since the previous snapshot are not repeated here.

| Check | Result |
| --- | --- |
| `dotnet build DotNetQuery.slnx -c Release` | 0 warnings, 0 errors |
| `dotnet test DotNetQuery.slnx -c Release` | **1,260 passed**, 12 skipped (the Redis tests: no `DOTNETQUERY_REDIS`), 0 failed: core 542 + 542, Blazor 62 + 62, SignalR 12 + 12, source generator 9, WPF/WinUI 4, Uno 1, Playwright E2E 14 |
| Realtime sample | exits 0, all checks pass (3 runs) |
| Trimmed self-contained AOT harness publish (with `Msynk.DotNetQuery.SignalR`) | 0 IL warnings, all checks pass; NativeAOT native link not available locally (CI job) |
| Trimmed Blazor WebAssembly publish (`TodoApp.Client`, now with the SignalR client) | 0 trim warnings from DotNetQuery assemblies |
| `dotnet list package --vulnerable --include-transitive` | clean (the SignalR package references the patched 10.0.14 client; 10.0.0 is flagged by GHSA-334f-vvp4-7g6p) |
| `dotnet pack` with package validation | 10 solution packages (new: `Msynk.DotNetQuery.SignalR`), no validation errors; `Msynk.DotNetQuery`'s dependencies unchanged |
| Not re-run | coverage report, DocFX site build, .NET 11 roll-forward, MAUI build, `eng/prepare-release.ps1` |

## Final verification snapshot (after items 42-55, 2026-11-04)

| Check | Result |
| --- | --- |
| `dotnet build DotNetQuery.slnx -c Release` (clean, bin/obj deleted) | 0 warnings, 0 errors |
| `dotnet test DotNetQuery.slnx -c Release`, Redis 7 available | **1,135 passed**, 0 skipped, 0 failed: core 496 on net8.0 + 496 on net10.0, Blazor bUnit 58 + 58, source generator 9, WPF/WinUI lifecycle 4, Uno 1, Playwright browser E2E 13; core suite clean over 10 more consecutive runs |
| Line coverage (cobertura) | core 89.3%, Blazor 81.1%, Blazor.Server 100%, source generator 93.9%, Sqlite 76.9%, Redis 65.2%, WPF 60.6% |
| Core suite on .NET 11 RC 3 (`DOTNET_ROLL_FORWARD=LatestMajor`) | 486 passed |
| Console samples (Chat, OfflineFirst, ECommerce, Dashboard), DevTools CLI (`emit` demo and `watch demo`) | all exit 0 |
| Avalonia headless verify, WPF verify | PASS, PASS |
| Trimmed self-contained AOT harness publish | 0 IL2xxx/IL3xxx warnings, all 37 checks pass (NativeAOT link: CI) |
| Trimmed Blazor WebAssembly publish (`TodoApp.Client`) | 0 trim warnings from DotNetQuery assemblies |
| MAUI adapter (net10.0-android) | 0 warnings, 0 errors |
| `dotnet pack` with package validation | 9 solution packages + MAUI, no validation errors |
| `dotnet list package --vulnerable --include-transitive` | clean |
| DocFX (`--warningsAsErrors`) | 0 warnings, 0 errors; 179 API pages including the Razor components |
| `eng/prepare-release.ps1` on a copy | ships 1,905 API entries and 3 analyzer rules, sets 0.9.0, dates the changelog |

## Open items and blockers

What remains needs the repository owner or hardware this machine does not have:

- **Owner actions:** reserve the `Msynk.` prefix on nuget.org; enable GitHub Pages (Settings > Pages > Source = GitHub
  Actions); configure nuget.org trusted publishing for `release.yml`, the `NUGET_USER` variable and the `nuget`
  environment; then cut 0.9.0 with `eng/prepare-release.ps1` and a `v0.9.0` tag.
- **Needs a Mac:** the MAUI iOS / Mac Catalyst heads (the project adds them automatically on macOS).
- **CI-only:** the NativeAOT native link (this machine lacks the VC++ linker; the ILC analysis and the trimmed
  self-contained publish are clean locally).
- **Not runnable here:** WinUI and Uno windows (they need the Windows App runtime or an Uno app head); their mappings
  are tested and their wiring is compile-verified.
