# DotNetQuery Documentation

Server-state management for .NET - fetching, caching, synchronizing, and mutating remote data with a framework-agnostic core.

## Guides

1. [Getting Started](getting-started.md) - install, register, first query, query definitions
2. [Blazor](blazor.md) - `<Query>`, hooks, prerender hydration, scoped clients, DevTools panel
3. [Caching](caching.md) - stale time, cache time, invalidation, cancellation, hydration
4. [Mutations](mutations.md) - lifecycle, invalidation, retries, guarantees
5. [Optimistic Updates](optimistic-updates.md) - instant UI with automatic rollback
6. [Infinite Queries](infinite-queries.md) - pagination (offset, cursor, bidirectional)
7. [Offline Mode](offline-mode.md) - pausing, resuming, in-memory and durable mutation queues
8. [Diagnostics](diagnostics.md) - event stream, OpenTelemetry traces & metrics, DevTools
9. [Performance Tips](performance.md) - deduplication, threading, trimming & NativeAOT
10. [Advanced Scenarios](advanced-scenarios.md) - persistence, transports, streaming, testing

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

## Packages

- **DotNetQuery.Core** - the portable engine (zero UI dependencies)
- **DotNetQuery.DependencyInjection** - `AddDotNetQuery`
- **DotNetQuery.Persistence** - cache persistence (in-memory + file system) and the durable mutation queue
- **DotNetQuery.Persistence.Sqlite** / **.Redis** / **.BlazorWasm** - SQLite, Redis, and browser localStorage/IndexedDB providers
- **DotNetQuery.Http** / **.GraphQL** - HTTP/JSON and GraphQL fetcher conveniences
- **DotNetQuery.DevTools** - inspection, event log, export
- **DotNetQuery.Blazor** / **DotNetQuery.DevTools.Blazor** - Blazor components
- **DotNetQuery.Mvvm** / **DotNetQuery.Wpf** / **DotNetQuery.WinUI** / **DotNetQuery.Avalonia** / **DotNetQuery.Maui** - XAML/MVVM bindings & adapters
