# DotNetQuery - Path to 1.0

Status: **feature-complete core and Blazor adapter, 6 UI adapters, 412 tests, 7 NuGet packages, seven
verified samples, a BenchmarkDotNet suite, a verified NativeAOT publish, CI, and a frozen public API
surface.** This document tracks what remains before a formal 1.0 tag. See [CHANGELOG.md](CHANGELOG.md) for
the latest changes.

## Done

- **Core engine** - `QueryClient`, `QueryCache`, structural/hierarchical `QueryKey`, reusable
  `QueryDefinition<T>`, immutable state/options, retry policies, deduplication with supersession
  (invalidation cancels outdated fetches), ordered observer notifications, cancellation with state
  restore, atomic cache updates, hydration, race-free background GC.
- **Mutations** - declarative optimistic updates with exact, conflict-safe, atomic multi-key rollback;
  `Invalidates`; retries with `ShouldRetry`; lifecycle callbacks that never re-run the mutation; cancellation;
  mutation keys and ids; the mutation cache (`client.Cache.Mutations`) and serial `Scope`s.
- **Query engine additions** - query filters and bulk operations, atomic multi-query transactions, initial and
  placeholder data, selectors with fine-grained notifications, opt-in structural sharing.
- **Infinite queries** - cache-backed (shared, invalidated, persisted, hydrated, inspected),
  offset/cursor/bidirectional, sliding window, cursor re-derivation on refetch.
- **Background sync** - interval, focus/reconnect/resume triggers, dependency invalidation.
- **Offline** - every refetch path pauses offline and resumes on reconnect; in-memory
  `OfflineMutationQueue` and durable `PersistentMutationQueue`, both strictly ordered.
- **Persistence** - pluggable `IQueryPersister` (in-memory, file system, SQLite, Redis, localStorage,
  IndexedDB) with timestamp-preserving restore, `MaxAge`, `Buster`, auto-persist, and trim-safe registration.
- **Blazor** - .NET 8 + .NET 10; scoped per-circuit clients; `<Query>` with templates, keep-previous-data,
  dependent queries and automatic prerender hydration; hook-style `UseQuery`/`UseMutation`/`UseInfiniteQuery`;
  browser signals; inline/floating DevTools panel.
- **Diagnostics** - event stream, OpenTelemetry `ActivitySource`/`Meter`, DevTools model with data
  previews and mutation history, CLI dashboard.
- **Adapters** - Blazor, MVVM, WPF, WinUI, Avalonia, MAUI.
- **Transport** - HTTP/JSON and GraphQL fetchers/definitions with source-generated (AOT-safe) overloads;
  `StreamIntoCacheAsync` for SignalR/channels/gRPC streams.
- **Trimming / NativeAOT** - Core, DependencyInjection, Persistence (all providers), Http and GraphQL are
  `IsAotCompatible`; the AOT harness exercises definitions, optimistic updates, persistence, the durable
  queue and HTTP queries.
- **Infra** - DI integration, multi-target net8.0/net10.0, NuGet packaging with symbols + SourceLink,
  10 doc guides, API freeze via PublicApiAnalyzers, CI (Windows build/test/pack, Linux tests with Redis,
  AOT publish, MAUI Android), BenchmarkDotNet suite.

## Remaining work before 1.0

| Item | Notes | Priority |
| --- | --- | --- |
| **Ship the API baselines** | Move `PublicAPI.Unshipped.txt` entries to `Shipped.txt` when tagging 1.0. | P1 |
| **Benchmark baseline** | A ShortRun baseline is committed; re-run with the default job on a quiet machine for the 1.0 baseline. | P2 |
| **MAUI iOS/macCatalyst heads** | Wired to build on macOS; needs a Mac build host. | P2 |
| **Query key source generator** | Optional compile-time key/definition factories. | P3 |
| **Reserve the `Msynk.` NuGet prefix** | Package IDs now use `Msynk.DotNetQuery.*` (the old IDs collided with an unrelated library); request prefix reservation on nuget.org. | P1 |

## Known characteristics (documented, not bugs)

- A `Mutation` instance's observable state tracks its most recent run; use one instance per independently
  observed operation (or the one-shot `client.MutateAsync`).
- Observers, `InfiniteQuery` and `ObservableQuery` instances must be disposed (Blazor components do this
  for you) - an undisposed observer keeps its query active.
- `PersistentMutationQueue` replays at-least-once: a crash between a successful request and its removal from
  the store replays it on the next start, so handlers should be idempotent.
- Queries are persisted by exact registered key or by key prefix; prefixed records are restored when the
  matching query is first used (a key cannot be rebuilt from its persisted text).
