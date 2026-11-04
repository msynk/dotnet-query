# DotNetQuery - Improvement Plan

The execution checklist for turning DotNetQuery into a production-ready server-state framework for .NET,
with Blazor as the flagship integration. Verified status, evidence and test results live in
[IMPLEMENTATION-STATUS.md](IMPLEMENTATION-STATUS.md).

Legend: `[x]` done and verified (built, tested) · `[~]` partially done (see status doc) · `[!]` blocked
(reason in status doc) · `[ ]` not started. An item is never checked because code was merely written.

Execution rules (abridged): inspect source before deciding something is missing; keep the core
framework-independent and trim/AOT-safe; every change ships with tests, docs and public API baselines;
no silent breaking changes; never fabricate results; work in small, independently verifiable steps.

Priorities: **P0** production readiness (correctness, structural sharing, selectors, initial/placeholder
data, mutation cache, transactions, auth isolation, E2E tests, performance baselines, naming) ·
**P1** competitive core (advanced query/mutation management, cross-tab sync, memory management, HTTP
conditional requests, offline queue reliability, pagination, Blazor lifecycle, DevTools) · **P2** ecosystem
(OpenAPI generation, GraphQL, real-time, platforms, time travel) · **P3** adoption (docs, guides, samples,
packaging).

## 2. Initial investigation

- [x] Inspect repository structure, docs, projects, public APIs, tests, benchmarks, CI, samples
- [x] Identify TODOs / incomplete implementations (none marked in source)
- [x] Compare documented capabilities against implementation and tests
- [x] Establish build and test baselines
- [x] Establish performance baseline (benchmark run, ShortRun)
- [x] Investigate NuGet naming collision
- [x] Create `IMPLEMENTATION-STATUS.md`

## 3. Query engine

- [x] Configurable structural sharing
- [x] Query selectors and fine-grained subscriptions
- [x] Initial-data and placeholder-data semantics
- [x] Initial-data timestamps and freshness
- [x] Dependent, parallel and composed queries (Enabled, FetchAllAsync, Task.WhenAll)
- [x] Reusable strongly typed query definitions (pre-existing `QueryDefinition<T>`)
- [x] Global and hierarchical query defaults
- [x] Query filtering by key, status, activity, staleness
- [x] Complete reset, removal, cancellation and refetch operations
- [x] Configurable garbage collection (CacheTime, GarbageCollectionInterval, MaxInactiveQueries, TrimOnMemoryPressure)
- [x] Notification batching and scheduling (QueryNotifications.Batch)
- [x] Cache snapshots and restoration
- [x] Cache inspection and subscriptions (FindAll, Describe, Changed, Watch/selectors)
- [x] Prefetching and prefetch cancellation (PrefetchAsync, CancelQueriesAsync)
- [x] Dependency tracking and coordinated invalidation (AddInvalidationDependency)
- [x] Predictable behavior across multiple observers (ordered delivery; 300-observer convergence stress test)
- [x] Verify loading/pending/success/error/stale semantics (FetchLifecycleTests, ResultAndPrefetchTests, QueryClientTests)
- [x] Error propagation and recovery (ThrowOnError, retry classification, error-is-stale refetch, observer/diagnostic isolation)
- [x] Lifecycle under cancellation and disposal (cancel restores state, cancel during back-off, disposal during notification)

## 4. Mutation engine

- [x] Comprehensive mutation cache
- [x] Independently observable mutation instances (one Mutation per operation + MutationCache)
- [x] Mutation defaults and hierarchical configuration
- [x] Mutation scopes and serial execution
- [x] Concurrent and parallel mutations (unscoped run concurrently; scoped run serially)
- [x] Mutation cancellation, reset, filtering, inspection
- [x] Optimistic updates and rollback improvements
- [x] Overlapping optimistic mutations never revert unrelated updates (optimistic layers with rebase)
- [x] Atomic cache updates and transactions
- [x] Mutation dependency chains and grouped operations (Scope ordering; atomic multi-key optimistic transactions)
- [x] Mutation persistence and restoration (the durable PersistentMutationQueue persists and replays writes, with dead letters; Mutation instances are per-process by design)
- [x] Mutation retention and garbage collection
- [x] Safe mutation deduplication (DeduplicatePending)
- [x] Global lifecycle callbacks
- [x] Mutation progress reporting
- [x] Network-aware mutation execution
- [x] Mutation expiration and dead-letter handling

## 5. Cache and synchronization

- [x] Structural sharing throughout cache operations (fetches, SetQueryData, updaters, hydration, synchronized data)
- [x] Cache size limits and memory-pressure management
- [x] Configurable eviction strategies (time-based, LRU size bound, memory pressure, manual Trim)
- [x] Cache partitioning by user, tenant, session, context (key-prefix partitions + filter-based removal/reset, scoped clients, ResetOnUserChange, QueryClientProvider for separate caches; tenant E2E)
- [x] Atomic multi-query transactions
- [x] Snapshots, restoration, selective rollback (CreateSnapshot/Restore, DevTools snapshots, transaction and optimistic-layer rollback)
- [x] Cache dependency graphs (invalidation dependencies)
- [x] Cross-tab and cross-window synchronization
- [x] BroadcastChannel (storage events not needed)
- [x] Cross-process synchronization abstractions
- [x] Selective persistence and exclusion rules (exact + prefix registrations, typed)
- [x] Persistence schema versioning and migration (Buster + Migrations)
- [x] Encryption hooks for persisted data
- [x] Persistence crash recovery (atomic file writes, corruption tolerance, durable queue replay)
- [x] Stale persistence writes never overwrite newer state (per-key write gate)
- [x] Blazor Server circuit and user isolation verified
- [x] Partition invalidation on authentication and tenant changes

## 6. HTTP and network

- [x] Network modes: online, offline-first, always
- [x] Pause, resume, reconnection behavior
- [x] Request priorities and concurrency limits
- [x] Request scheduling and rate limiting (IFetchRateLimiter)
- [x] Timeout policies
- [x] Cancellation propagation
- [x] HTTP-aware retry classification
- [x] `Retry-After`
- [x] Circuit-breaker integration (CircuitBreakerHandler)
- [x] Request batching and coalescing (BatchLoader; dedup coalesces identical requests)
- [x] ETag / conditional requests, `If-None-Match`, 304
- [x] Cache-control semantics (ETag/Last-Modified revalidation, no-store respected; freshness stays per query via StaleTime)
- [x] Request middleware / interceptors (IQueryInterceptor; DelegatingHandler for HTTP)
- [x] Authentication token refresh integration (BearerTokenHandler)
- [x] Custom transport abstractions (fetchers are delegates; IQuerySyncChannel)
- [x] Streaming and incremental responses (StreamIntoCacheAsync)
- [x] Configurable and adaptive polling
- [x] Transport vs application errors (HttpRetryPolicy.IsTransient, GraphQLErrors)

## 7. Pagination and infinite queries

- [x] Offset and cursor pagination complete
- [x] Forward and backward pagination
- [x] Page prefetching (FetchPagesAsync, PrefetchInfiniteQueryAsync)
- [x] Maximum page counts and eviction (MaxPages sliding window)
- [x] Page retention and restoration (cache-backed, persisted, hydrated)
- [x] Concurrent page fetching with deterministic order (single cycle per key, ordered refetch)
- [x] Automatic page invalidation
- [x] Dynamic page-size changes (page size in the key; documented)
- [x] Pagination metadata (PageCount, HasNext/Previous, IsFetchingNext/Previous)
- [x] Scroll restoration (ScrollRestoration component)
- [x] Virtualized list integration (ToItemsProvider)
- [x] Persistence/hydration of infinite queries
- [x] Large-dataset examples (TodoApp `/large`: 10,000 rows, Virtualize + infinite query)

## 8. Offline-first

- [x] Durable mutation queues improvements
- [x] Idempotency keys
- [x] Expiration and dead-letter handling
- [x] Authentication-aware replay (Pause/ResumeAsync, ClearAsync)
- [x] Configurable retry and replay policies
- [x] Queue inspection and management APIs
- [x] Conflict detection and resolution (IsConflict + OnReplayFailure; optimistic conflicts reconciled)
- [x] Mutation ordering and dependencies (strict queue order, scopes)
- [x] Connectivity transition handling
- [x] Offline cache availability policies (NetworkMode, persistence with MaxAge)
- [x] Partial offline operation (per-query/per-mutation NetworkMode)
- [x] Crash-recovery and duplicate-delivery tests
- [x] Document at-least-once delivery and server idempotency

## 9. Blazor

- [x] Polished `UseQuery` / `UseMutation` / `UseInfiniteQuery` (+ UseQueries, selectors, hydration)
- [x] Declarative Razor query definitions
- [x] Provider / cascading context (QueryClientProvider)
- [x] Declarative dependent queries
- [x] Parallel-query orchestration (UseQueries, FetchAllAsync)
- [x] Selectors and fine-grained subscriptions in components
- [x] Automatic observer disposal (verify)
- [x] Rendering and notification optimization (NotifyOn, selectors, renderer coalescing)
- [x] Loading, error, empty, placeholder, stale templates (Loading/Error/Empty templates; placeholder via result; stale via result.IsFetching/IsRefetchError)
- [x] Error boundary integration
- [x] Reset / refetch / invalidation APIs
- [x] Verify Interactive Server, WebAssembly, Auto (per-page render modes in TodoApp, Playwright)
- [x] Static SSR and hydration improvements (static `/ssr` page; server + WebAssembly prerender hydration)
- [x] Streaming SSR (`[StreamRendering]` streams Loading then data; E2E verified)
- [x] No duplicate requests during prerendering (hooks included)
- [x] Navigation-aware refetching and cancellation (DotNetQueryNavigation)
- [x] Visibility and focus events
- [x] Server reconnection handling (CircuitHandler pattern, sample + docs)
- [x] Authentication and circuit lifecycle (ResetOnUserChange)
- [x] Cache isolation between users and circuits (scoped clients + user-change reset)
- [x] Browser-only APIs never break server execution (JS calls guarded; prerender-safe components)
- [x] Virtualize, QuickGrid, EditForm, AuthenticationStateProvider, NavigationManager, SignalR integrations (SignalR as a documented pattern)
- [x] WebAssembly offline / service-worker scenarios (offline E2E; PWA guidance in offline-mode.md)

## 10. Cross-platform

- [x] MAUI, WPF, WinUI, Avalonia improvements (lifecycle classes, infinite-query factories)
- [x] MVVM observable state improvements (change-only notifications, initial/placeholder data, commands, ObservableInfiniteQuery)
- [x] Lifecycle and connectivity events (AppLifecycleSignals, NetworkConnectivityMonitor, per-platform lifecycle classes)
- [x] Platform persistence adapters (file system everywhere, MAUI app-data helper, SQLite; browser storage for Blazor)
- [x] Consistent APIs; runnable examples (same factory/lifecycle shape on all four; Avalonia headless-verified, MAUI builds)

## 11. DevTools

- [x] Live query and mutation inspection improvements
- [x] Lifecycle timelines
- [x] Key hierarchy and dependency visualization (key tree; dependencies visible as invalidation events in timelines)
- [x] Request/response inspection (data previews, error messages, timings; HTTP-level via DelegatingHandlers/logging)
- [x] Retries, cancellation, refetch visualization
- [x] Optimistic update / rollback history
- [x] Snapshots and restoration; controlled state editing
- [x] Profiling
- [x] Persistence and offline queue inspection (queue tab; persisted keys via manager APIs)
- [x] Search and advanced filtering
- [x] Diagnostic report export
- [x] Time-travel investigation (snapshots + restore)
- [x] Sensitive-data protection and production exposure control (panel Enabled/ShowData)
- [x] CLI diagnostics (profiles)

## 12. OpenAPI and productivity

- [x] Roslyn source generator investigation (implemented: `[GenerateQueries]`, shipped in the core package)
- [x] OpenAPI-generated queries, keys, mutations, invalidation (via Refitter/NSwag interfaces + assembly-level attribute)
- [x] Refit / HttpClient integration (Refit verb attributes classify methods)
- [x] GraphQL improvements (mutations, error path/code)
- [x] Generated code trim/AOT verification (AOT harness; zero-warning consumer compile test)

## 13. Performance

- [x] Benchmarks: lookup, hashing, dedup, notification, memory per entry, structural sharing, selectors,
      transactions, GC/eviction, large updates, infinite memory, persistence throughput, Blazor rendering
- [x] Comparison with representative .NET caches (CacheComparisonBenchmarks: ConcurrentDictionary, MemoryCache)
- [x] Hot-path allocation optimization (fresh hit 488 -> 32 B/op; SetQueryData 216 -> 120 B/op)
- [x] Regression detection (AllocationBudgetTests in every test run)

## 14. Reliability, security, compatibility

- [x] Stress tests: hundreds of observers, concurrent invalidation/refetch, overlapping optimistic
      mutations, cancellation during retries, disposal during notification, eviction during operations,
      hydration races, offline transitions during mutations, shutdown during persistence (ReliabilityStressTests, OptimisticLayerTests)
- [x] Authentication and tenant isolation verified (scoped circuits, ResetOnUserChange tests, tenant E2E)
- [x] Diagnostic events never expose secrets (IncludeVariablesInDiagnostics, DiagnosticKeyFormatter, no data in events)
- [x] Production DevTools security (Enabled/ShowData)
- [x] NativeAOT and trimming verified (trimmed publish locally with 0 IL warnings incl. generated code; native link via the CI aot job)
- [x] All target frameworks validated (core and Blazor tests on net8.0 and net10.0)
- [x] Public API baselines maintained (PublicAPI.Unshipped.txt for every package, enforced by the build)
- [x] Dependency and vulnerability scanning (CI step; one sample advisory fixed)

## 15. Testing and CI

- [x] Unit tests for new public APIs
- [x] Deterministic virtual-time tests (FakeTimeProvider across staleness, intervals, signals, GC)
- [x] Concurrency tests (ConcurrencyTests, ConcurrencyRegressionTests, ReliabilityStressTests)
- [x] Mutation / optimistic scenario tests (MutationTests, MutationBehaviorTests, OptimisticLayerTests, E2E rollback)
- [x] Persistence and crash-recovery tests (DurableQueueReliabilityTests, crash replay, shutdown-during-persist stress)
- [x] Cross-tab browser tests
- [x] Playwright end-to-end tests
- [x] All Blazor render modes tested (Server, WebAssembly, Auto, static streaming SSR)
- [x] Desktop / MAUI tests where possible (Avalonia headless lifecycle + command check in CI; MAUI Android build; iOS/Mac need a Mac)
- [x] NativeAOT publish and execution tests (CI aot job publishes and runs the harness)
- [x] Trimming tests (trimmed self-contained harness publish, 0 IL warnings, all checks pass)
- [x] Performance regression tests (AllocationBudgetTests)
- [x] Samples validated in CI (console samples run, Avalonia headless, TodoApp smoke + Playwright)

## 16. Documentation and packaging

- [x] Documentation site (DocFX config + Pages workflow; built locally)
- [x] Architecture and lifecycle docs (docs/architecture.md)
- [x] Every public API and option documented (XML docs enforced by warnings-as-errors; DocFX API reference)
- [x] TanStack Query migration guide (docs/tanstack-migration.md)
- [x] Caching, invalidation, synchronization docs (caching.md, advanced-scenarios.md)
- [x] Offline guarantees docs
- [x] SSR and hydration docs (blazor.md: prerendering, render modes, streaming SSR)
- [x] Tutorials and samples (Blazor Server/WASM/Web App, MAUI/desktop, CRUD, auth, multi-tenant,
      infinite scrolling, optimistic/conflict, DevTools) (docs/tutorials.md + samples)
- [x] Troubleshooting and performance guides (troubleshooting.md, performance.md with measured numbers)
- [x] Package strategy (Msynk.-prefixed IDs; features without extra dependencies stay in the core package)
- [x] Naming and discoverability collision (Msynk.DotNetQuery.* package IDs)
- [x] Minimize transitive dependencies (core: two Microsoft.Extensions abstractions; generator bundled, not a dependency)
- [x] Package metadata, icon, README, SourceLink, symbols (validated in the packed .nupkg files; packed generator smoke-tested)
- [x] Semantic versioning and release policy (docs/releasing.md)
- [x] Migration guides between releases (docs/migration.md)

## 17. Gaps found by the documentation review (2026-11-04)

- [x] Public API baselines for the Blazor package (PublicApiAnalyzers on every package)
- [x] Trimmed Blazor WebAssembly verified (adapter `IsAotCompatible`, trim-safe hydration, CI publish gate)
- [x] `MutationOptions.RetryCount` / `InfiniteQueryOptions.RetryCount` nullable (explicit opt-out of default retries)
- [x] Durable queue shared by several tabs/processes replays each write once (`ReplayLock`, Web Locks, lock file)
- [x] Release automation (tag-driven workflow, trusted publishing, provenance, `eng/prepare-release.ps1`)
- [x] Repository hygiene (security policy, code of conduct, issue/PR templates, Dependabot)
- [x] Framework policy (LTS targets, forward-compatibility CI job on the newest runtime; WPF/WinUI on .NET 8)
- [x] Code coverage in CI
- [x] Package validation
- [x] Analyzer release tracking shipped by the release script
- [x] WPF/WinUI lifecycle runtime tests and a WPF sample
- [x] DocFX API reference includes the Razor components
- [x] Live DevTools for non-Blazor apps (`QueryDevToolsServer` + CLI `watch`)
- [x] `ILogger` integration (`AddDiagnosticLogging`)
- [x] Redis pub/sub synchronization channel; SignalR recipe
- [x] Blazor Server reconnection shipped as a package (`Msynk.DotNetQuery.Blazor.Server`)
- [x] Uno Platform adapter; Blazor Hybrid guidance
- [x] Generator: classes (Kiota wrappers), generic methods, infinite queries
- [x] Query and mutation `Meta`
- [x] Samples match the tutorials (durable queue in OfflineFirst, declarative optimistic updates in ECommerce)
- [x] Refreshes wait for pending optimistic updates (no flash of server data during concurrent mutations)
- [x] `DeduplicatePending` race (a call right after completion joined the settled run)
- [x] Mutations settling together refetch once (invalidation joins a refetch started after the server call)
- [ ] Owner actions: reserve the `Msynk.` prefix, enable GitHub Pages, configure nuget.org trusted publishing (see ROADMAP)
