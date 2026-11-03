# DotNetQuery Documentation

Server-state management for .NET - fetching, caching, synchronizing, and mutating remote data with a framework-agnostic core.

## Guides

The same guides are published as a website with an API reference (built by DocFX from `docs/docfx.json`, see
`.github/workflows/docs.yml`).

1. [Getting Started](getting-started.md) - install, register, first query, query definitions
2. [Tutorials](tutorials.md) - step-by-step paths: CRUD, render modes, auth and tenants, infinite scrolling, offline-first, desktop and mobile, DevTools
3. [Blazor](blazor.md) - `<Query>`, hooks, prerender hydration, render modes, scoped clients, DevTools panel
4. [Caching](caching.md) - stale time, cache time, invalidation, cancellation, hydration
5. [Mutations](mutations.md) - lifecycle, invalidation, retries, guarantees
6. [Optimistic Updates](optimistic-updates.md) - instant UI with automatic rollback
7. [Infinite Queries](infinite-queries.md) - pagination (offset, cursor, bidirectional)
8. [Offline Mode](offline-mode.md) - pausing, resuming, in-memory and durable mutation queues
9. [Diagnostics](diagnostics.md) - event stream, OpenTelemetry traces & metrics, DevTools
10. [Performance Tips](performance.md) - deduplication, threading, trimming & NativeAOT
11. [Advanced Scenarios](advanced-scenarios.md) - persistence, transports, streaming, synchronization, testing
12. [Generated queries (Refit, OpenAPI)](source-generation.md) - `[GenerateQueries]`: keys, definitions and mutations from an API interface
13. [Desktop and mobile](desktop-and-mobile.md) - WPF, WinUI, Avalonia, MAUI: MVVM bindings, commands, lifecycle refetch
14. [Architecture and lifecycle](architecture.md) - how a fetch, a mutation and a notification flow through the engine
15. [Troubleshooting](troubleshooting.md) - symptoms, causes, fixes
16. [Coming from TanStack Query](tanstack-migration.md) - concept and API mapping
17. [Migrating between releases](migration.md) - package IDs, behavior changes
18. [Versioning and releases](releasing.md) - semantic versioning, what counts as breaking, release checklist

## Core concepts at a glance

| Concept | Type | Purpose |
| --- | --- | --- |
| Query key | `QueryKey` | Immutable, hierarchical identity for a query |
| Query definition | `QueryDefinition<T>` | Reusable key + fetcher + options, type-safe everywhere |
| Client | `IQueryClient` / `QueryClient` | Orchestrates fetching, caching, invalidation |
| State | `QueryState<T>` / `QueryResult<T>` | Immutable snapshot of a query |
| Options | `QueryOptions` / `QueryClientOptions` | Per-query and global configuration |
| Mutation | `Mutation<TVars, TData>` | Writes with optimistic updates, rollback, invalidation |
| Optimistic update | `OptimisticUpdate` | Declarative cache write rolled back automatically on failure |
| Infinite query | `InfiniteQuery<TParam, TPage>` | Paginated reads |
| Observer | `QueryObserver<T>` | Live subscription to a query's state |
| Offline queues | `OfflineMutationQueue` / `PersistentMutationQueue` | Ordered replay of writes after reconnect |
| Query filter | `QueryFilter` | Select queries by key, activity, staleness, status for bulk operations |
| Transaction | `SetQueriesData` / `QueryDataCommit` | Atomic multi-query writes with rollback |
| Mutation cache | `client.Cache.Mutations` | Inspect, filter and count mutation runs; global hooks |
| Synchronizer | `QuerySynchronizer` / `IQuerySyncChannel` | Keep caches consistent across tabs, windows, processes |

## Packages

Published with the owner-scoped `Msynk.` prefix (for example `Msynk.DotNetQuery.Blazor`); namespaces and
assemblies are named without it.

- **DotNetQuery** - the portable engine (zero UI dependencies) plus `AddDotNetQuery`, cache persistence
  (in-memory + file system) and the durable mutation queue, HTTP/JSON and GraphQL fetchers, MVVM bindings,
  and the DevTools model (inspection, event log, export)
- **DotNetQuery.Blazor** - Blazor components, browser localStorage/IndexedDB persisters, and the DevTools panel
- **DotNetQuery.Persistence.Sqlite** / **.Redis** - SQLite and Redis persistence providers
- **DotNetQuery.Wpf** / **DotNetQuery.WinUI** / **DotNetQuery.Avalonia** / **DotNetQuery.Maui** - XAML adapters (see [Desktop and mobile](desktop-and-mobile.md))
  over the MVVM bindings
