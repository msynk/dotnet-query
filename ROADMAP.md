# DotNetQuery - Path to 1.0

Status: **feature-complete core and Blazor adapter, 8 UI adapters (Blazor, Blazor Server, MVVM, WPF, WinUI, Avalonia,
MAUI, Uno), 1,135 tests, 10 NuGet packages, eight verified samples, a BenchmarkDotNet suite, a verified NativeAOT
publish and trimmed Blazor WebAssembly publish, CI with coverage, a tag-driven release workflow, and a frozen public API
surface for every package.** This document tracks what remains before a formal 1.0 tag. See
[CHANGELOG.md](CHANGELOG.md) for the latest changes and [IMPLEMENTATION-STATUS.md](IMPLEMENTATION-STATUS.md) for the
verified evidence.

## Done

- **Core engine** - `QueryClient`, `QueryCache`, structural/hierarchical `QueryKey`, reusable
  `QueryDefinition<T>`, immutable state/options, retry policies, deduplication with supersession
  (invalidation cancels outdated fetches), ordered observer notifications, cancellation with state
  restore, atomic cache updates, hydration, race-free background GC, query and mutation `Meta`.
- **Mutations** - declarative optimistic updates with exact, conflict-safe, atomic multi-key rollback and overlapping
  layers; refreshes of a query wait while its optimistic updates are pending, then run once; `Invalidates` that join a
  refetch started after the mutation's server call (mutations settling together refetch once); retries with `ShouldRetry` (a mutation can opt out of default retries with `RetryCount = 0`);
  lifecycle callbacks that never re-run the mutation; cancellation; mutation keys and ids; the mutation cache
  (`client.Cache.Mutations`) and serial `Scope`s.
- **Query engine additions** - query filters and bulk operations, atomic multi-query transactions, initial and
  placeholder data, selectors with fine-grained notifications, opt-in structural sharing.
- **Infinite queries** - cache-backed (shared, invalidated, persisted, hydrated, inspected),
  offset/cursor/bidirectional, sliding window, cursor re-derivation on refetch.
- **Background sync** - interval, focus/reconnect/resume triggers, dependency invalidation.
- **Offline** - every refetch path pauses offline and resumes on reconnect; in-memory
  `OfflineMutationQueue` and durable `PersistentMutationQueue`, both strictly ordered; the durable queue coordinates
  instances sharing a store (browser tabs over IndexedDB with `WebLocksMutationQueueLock`, processes with
  `FileMutationQueueLock`), so each queued write is replayed once.
- **Persistence** - pluggable `IQueryPersister` (in-memory, file system, SQLite, Redis, localStorage,
  IndexedDB) with timestamp-preserving restore, `MaxAge`, `Buster`, auto-persist, and trim-safe registration.
- **Synchronization** - `QuerySynchronizer` over `BroadcastChannel` (tabs), in-memory hubs and Redis pub/sub
  (`RedisQuerySyncChannel`, server instances); a SignalR transport recipe.
- **Blazor** - .NET 8 + .NET 10; scoped per-circuit clients; `<Query>` with templates, keep-previous-data,
  dependent queries and automatic prerender hydration (trim-safe with `UseHydrationJson`); hook-style
  `UseQuery`/`UseMutation`/`UseInfiniteQuery`; browser signals; inline/floating DevTools panel; Blazor Server circuit
  reconnection (`Msynk.DotNetQuery.Blazor.Server`); Blazor Hybrid guidance.
- **Diagnostics** - event stream, `ILogger` integration (`AddDiagnosticLogging`), OpenTelemetry `ActivitySource`/`Meter`,
  DevTools model with data previews and mutation history, a live DevTools endpoint (`QueryDevToolsServer`) for any app
  and the CLI dashboard (`watch` mode).
- **Adapters** - Blazor, Blazor Server, MVVM, WPF and WinUI (.NET 8 and 10), Avalonia, MAUI, Uno Platform.
- **Transport** - HTTP/JSON and GraphQL fetchers/definitions with source-generated (AOT-safe) overloads;
  `StreamIntoCacheAsync` for SignalR/channels/gRPC streams.
- **Source generator** - `[GenerateQueries]` on interfaces and classes (Refit, NSwag/Refitter, Kiota wrappers):
  keys, definitions, mutations, generic methods and infinite queries (`[InfiniteQueryMethod]`).
- **Trimming / NativeAOT** - the core, Blazor, Blazor Server and persistence packages are `IsAotCompatible` (the XAML
  adapters are not annotated, like their UI frameworks); the AOT harness exercises definitions, optimistic updates, persistence, the durable queue with a
  lock, logging, generated queries and HTTP queries; CI checks a trimmed Blazor WebAssembly publish.
- **Infra** - DI integration, multi-target net8.0/net10.0, NuGet packaging with symbols + SourceLink + package
  validation, 18 doc guides and a DocFX site whose API reference includes the Razor components, API freeze via
  PublicApiAnalyzers on every package, CI (Windows build/test/pack with coverage, Linux tests with Redis,
  latest-runtime forward-compatibility tests, AOT publish, trimmed WebAssembly publish, MAUI Android, WPF/Avalonia
  smoke tests), a tag-triggered release workflow (nuget.org trusted publishing, build provenance, GitHub release),
  `eng/prepare-release.ps1`, Dependabot, security policy, code of conduct, issue/PR templates.

## Remaining work before 1.0

| Item | Notes | Priority |
| --- | --- | --- |
| **Reserve the `Msynk.` NuGet prefix** | Owner action on nuget.org: request prefix reservation for the `Msynk.DotNetQuery*` IDs. | P1 |
| **Enable GitHub Pages** | Owner action: Settings > Pages > Source = GitHub Actions, so `docs.yml` publishes the site. | P1 |
| **Configure trusted publishing** | Owner action: a nuget.org trusted publishing policy for `release.yml`, the `NUGET_USER` repository variable and the `nuget` environment (see the header of `release.yml`). | P1 |
| **First public release (0.9.0)** | Run `eng/prepare-release.ps1 -Version 0.9.0`, commit, tag `v0.9.0`. 0.8.0 was never published, so 0.9.0 is the first version on nuget.org. | P1 |
| **Package validation baseline** | After 0.9.0 is on nuget.org, set `PackageValidationBaselineVersion` to it in `Directory.Build.props`, so later releases are checked for binary compatibility. | P1 |
| **Ship the API baselines for 1.0** | `eng/prepare-release.ps1 -Version 1.0.0` moves `PublicAPI.Unshipped.txt` and `AnalyzerReleases.Unshipped.md` into the shipped files. | P1 |
| **Benchmark baseline** | A ShortRun baseline is committed; re-run with the default job on a quiet machine for the 1.0 baseline. | P2 |
| **MAUI iOS/macCatalyst heads** | Wired to build on macOS; needs a Mac build host. | P2 |
| **.NET 11 target** | .NET 11 (STS) is validated by the `test-latest-runtime` CI job (the net10.0 build rolled forward). Add a `net11.0` target only if an API needs it; otherwise the LTS targets cover STS releases. | P3 |

## Backlog (after 1.0)

| Item | Notes |
| --- | --- |
| Visual DevTools for XAML apps | The live endpoint and CLI cover any app; an in-app panel for WPF/WinUI/Avalonia/MAUI/Uno would match the Blazor one. |
| SignalR synchronization package | A ready-made `IQuerySyncChannel` + hub (recipe in the advanced scenarios guide today). |
| Native Kiota request-builder generation | Kiota clients are supported through a thin wrapper class; generating from request builders directly would remove it. |

## Known characteristics (documented, not bugs)

- A `Mutation` instance's observable state tracks its most recent run; use one instance per independently
  observed operation (or the one-shot `client.MutateAsync`).
- Observers, `InfiniteQuery` and `ObservableQuery` instances must be disposed (Blazor components do this
  for you) - an undisposed observer keeps its query active.
- `PersistentMutationQueue` replays at-least-once: a crash between a successful request and its removal from
  the store replays it on the next start, so handlers should be idempotent. Instances sharing a store need a
  `ReplayLock`.
- Queries are persisted by exact registered key or by key prefix; prefixed records are restored when the
  matching query is first used (a key cannot be rebuilt from its persisted text).
- Removals are not synchronized between caches; prefix synchronization updates only queries the receiving cache holds.
- `NetworkConnectivityMonitor` reports whether a network interface is up, not whether the server is reachable.
- Structural sharing is opt-in: it relies on `Equals` meaning full value equality.
- In a trimmed app that trims its own assemblies, prerender hydration needs `UseHydrationJson` for the app's types
  (Blazor's default partial trimming keeps them).
