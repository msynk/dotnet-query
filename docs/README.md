# DotNetQuery Documentation

Server-state management for .NET - fetching, caching, synchronizing, and mutating remote data with a framework-agnostic core.

## Guides

1. [Getting Started](getting-started.md) - install, register, first query
2. [Caching](caching.md) - stale time, cache time, invalidation, garbage collection
3. [Mutations](mutations.md) - creating, updating, deleting; callbacks and retries
4. [Optimistic Updates](optimistic-updates.md) - instant UI with rollback on failure
5. [Infinite Queries](infinite-queries.md) - pagination (offset, cursor, bidirectional)
6. [Offline Mode](offline-mode.md) - pausing, resuming, and queuing while offline
7. [Diagnostics](diagnostics.md) - event stream, OpenTelemetry traces & metrics, DevTools
8. [Performance Tips](performance.md) - deduplication, allocations, threading, AOT
9. [Advanced Scenarios](advanced-scenarios.md) - persistence, cancellation policies, testing your queries

## Core concepts at a glance

| Concept | Type | Purpose |
| --- | --- | --- |
| Query key | `QueryKey` | Immutable, hierarchical identity for a query |
| Client | `IQueryClient` / `QueryClient` | Orchestrates fetching, caching, invalidation |
| State | `QueryState<T>` / `QueryResult<T>` | Immutable snapshot of a query |
| Options | `QueryOptions` / `QueryClientOptions` | Per-query and global configuration |
| Mutation | `Mutation<TVars, TData>` | Writes with optimistic updates and rollback |
| Infinite query | `InfiniteQuery<TParam, TPage>` | Paginated reads |
| Observer | `QueryObserver<T>` | Live subscription to a query's state |

## Packages

- **DotNetQuery.Core** - the portable engine (zero UI dependencies)
- **DotNetQuery.DependencyInjection** - `AddDotNetQuery`
- **DotNetQuery.Persistence** - cache persistence (in-memory + file system)
- **DotNetQuery.Persistence.Sqlite** / **.Redis** / **.BlazorWasm** - SQLite, Redis, and browser localStorage providers
- **DotNetQuery.Http** / **.GraphQL** - HTTP/JSON and GraphQL fetcher conveniences
- **DotNetQuery.DevTools** - inspection, event log, export
- **DotNetQuery.Blazor** / **DotNetQuery.DevTools.Blazor** - Blazor components
- **DotNetQuery.Mvvm** / **DotNetQuery.Wpf** / **DotNetQuery.WinUI** / **DotNetQuery.Avalonia** / **DotNetQuery.Maui** - XAML/MVVM bindings & adapters
