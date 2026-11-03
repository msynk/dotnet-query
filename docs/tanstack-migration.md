# Coming from TanStack Query

DotNetQuery follows TanStack Query's model - server state lives in a keyed cache, queries are fetched,
deduplicated, cached, invalidated and refetched, mutations change server state and reconcile the cache - but it
is designed for .NET: `Task` and `CancellationToken` instead of promises and `AbortSignal`, immutable records
instead of plain objects, dependency injection instead of a provider component, and Razor components or MVVM
bindings instead of React hooks. This page maps the concepts.

## Core objects

| TanStack Query | DotNetQuery | Notes |
| --- | --- | --- |
| `QueryClient` | `IQueryClient` / `QueryClient` | Registered with `AddDotNetQuery` (singleton) or `AddDotNetQueryBlazor` (scoped per circuit / tab). |
| `QueryClientProvider` | DI | Components get the client via `@inject IQueryClient` or `DotNetQueryComponentBase`. |
| `queryKey: ['todos', id]` | `QueryKey.Create("todos", id)` | Structural, hierarchical, hashed once. `Append` derives child keys. |
| `queryOptions({ queryKey, queryFn })` | `QueryDefinition<T>` | Key + fetcher + options, typed. Use definitions everywhere. |
| `queryFn: ({ signal }) => ...` | `Func<CancellationToken, Task<T>>` | The token is cancelled on supersession, `CancelQueriesAsync`, or opt-in unobserved cancellation. |
| `useQuery` | `<Query Definition="...">` or `UseQuery(definition)` | Hook-style `Use*` on `DotNetQueryComponentBase`. |
| `useInfiniteQuery` | `CreateInfiniteQuery` / `UseInfiniteQuery` | `InfiniteData<TParam, TPage>` lives in the cache. |
| `useMutation` | `CreateMutation` / `UseMutation` | `MutateAsync` returns a `MutationResult`. |
| `QueryObserver` | `QueryObserver<T>` (`client.Watch(...)`) | Every transition, in order; dispose to unsubscribe. |
| `MutationCache` | `client.Cache.Mutations` | `FindAll(MutationFilter)`, `CountPending`, `Changed`. |
| Devtools | `<QueryDevToolsPanel />`, `QueryDevTools`, CLI | `Enabled` / `ShowData` control exposure. |

## Query options

| TanStack Query | DotNetQuery |
| --- | --- |
| `staleTime` | `QueryOptions.StaleTime` / `QueryClientOptions.DefaultStaleTime` (default zero; `Timeout.InfiniteTimeSpan` = never stale) |
| `gcTime` (formerly `cacheTime`) | `CacheTime` / `DefaultCacheTime` (default 5 minutes) |
| `retry`, `retryDelay` | `RetryCount` + `RetryDelay` (`RetryPolicy.*`) + `ShouldRetry` (`HttpRetryPolicy.ShouldRetry` for HTTP) |
| `enabled` | `Enabled` (dependent queries: `WithOptions(o => o with { Enabled = id is not null })`) |
| `refetchInterval` | `RefetchInterval` |
| `refetchOnWindowFocus`, `refetchOnReconnect` | `RefetchOnFocus`, `RefetchOnReconnect`, plus `RefetchOnResume`; signals come from `<DotNetQueryBrowserSignals />` or platform adapters |
| `networkMode: 'online' \| 'always' \| 'offlineFirst'` | `NetworkMode.Online \| Always \| OfflineFirst` (queries default `Online`, mutations default `Always`) |
| `initialData`, `initialDataUpdatedAt` | `QueryDefinition<T>.InitialData`, `InitialDataUpdatedAt`, `WithInitialData(...)` |
| `placeholderData` | `QueryDefinition<T>.PlaceholderData`, `WithPlaceholderData(...)`, `QueryResult<T>.WithPlaceholderData` |
| `placeholderData: keepPreviousData` | `<Query KeepPreviousData>`, `QueryResult<T>.KeepPreviousData(previous)`, `ObservableQuery.ChangeQuery(..., keepPreviousData: true)` |
| `select` | `client.Watch(key, select, notifyOn)` |
| `notifyOnChangeProps` | `QueryStateProperties` flags on `Watch` |
| `structuralSharing` | `StructuralSharing` / `DefaultStructuralSharing` - **off by default**, because it relies on `Equals` meaning full value equality |
| `throwOnError` | `ThrowOnError` (Blazor surfaces it to `ErrorBoundary`) |

## Query state

| TanStack Query | DotNetQuery (`QueryResult<T>` / `QueryState<T>`) |
| --- | --- |
| `status: 'pending'` | `IsPending` (`Idle` or `Loading`) |
| `isLoading` (first load in flight) | `IsLoading` |
| `isFetching` | `IsFetching` |
| `isRefetching` | `IsRefetching` |
| `isSuccess`, `isError` | `IsSuccess`, `IsError` (plus `IsLoadingError`, `IsRefetchError`) |
| `fetchStatus: 'paused'` | `IsPaused` |
| `dataUpdatedAt`, `errorUpdatedAt`, `failureCount` | `DataUpdatedAt`, `ErrorUpdatedAt`, `FailureCount` |
| `isPlaceholderData` | `IsPlaceholderData` |

## Client methods

| TanStack Query | DotNetQuery |
| --- | --- |
| `fetchQuery` | `FetchAsync(definition)` (returns a result; `EnsureData()` throws the error) |
| `prefetchQuery` | `PrefetchAsync(definition)` |
| `ensureQueryData` | `EnsureQueryDataAsync(definition)` |
| `getQueryData` | `GetQueryData(definition)` |
| `setQueryData(key, updater)` | `SetQueryData(key, updater)` - atomic, re-run on contention |
| `setQueriesData(filters, updater)` | `SetQueriesData<T>(filter, updater)` - atomic across all matches |
| *(no equivalent)* | `SetQueriesData(tx => tx.Set(...).Update(...))` - atomic multi-key transaction with `Rollback()` |
| `invalidateQueries({ queryKey, refetchType })` | `InvalidateQueriesAsync(key)` or `InvalidateQueriesAsync(QueryFilter, RefetchType)` |
| `refetchQueries` | `RefetchQueriesAsync(filter)` (single key: `RefetchQueryAsync`) |
| `cancelQueries` | `CancelQueriesAsync(key \| predicate \| filter)` |
| `resetQueries` | `ResetQueriesAsync(filter)` (restores initial data) |
| `removeQueries` | `RemoveQueries(filter)` / `RemoveQuery(key)` - an observed query is reset and refetched instead of stranding its observers |
| `isFetching()` | `CountFetching(filter)` |
| `isMutating()` | `Cache.Mutations.CountPending(filter)` |
| `getQueryCache().findAll(filters)` | `Cache.FindAll(QueryFilter)` |
| Query filters (`exact`, `type`, `stale`, `fetchStatus`, `predicate`) | `QueryFilter` (`Exact`, `Type`, `Stale`, `Fetching`, `Status`, `Predicate`) |
| `hydrate` / `dehydrate` | `HydrateQueryData`, Blazor prerender hydration (automatic), `QueryPersistenceManager` |
| `persistQueryClient` | `QueryPersistenceManager` + an `IQueryPersister` (file, SQLite, Redis, localStorage, IndexedDB), `ProtectedQueryPersister` for encryption |
| `broadcastQueryClient` (experimental) | `QuerySynchronizer`, Blazor `AddCrossTabSync` |

## Mutations

| TanStack Query | DotNetQuery |
| --- | --- |
| `onMutate` returning context | `OnMutate` (context flows to `OnSuccess` / `OnError` / `OnSettled`) |
| Optimistic update in `onMutate` + rollback in `onError` | Declarative `OptimisticUpdates` - cancel, snapshot, apply atomically, exact rollback |
| `onSettled: () => invalidateQueries(...)` | `Invalidates = [keys]` (the mutation stays pending until active queries refetched) |
| `retry` | `RetryCount`, `RetryDelay`, `ShouldRetry` (default: no retries - mutations are not idempotent by default) |
| `scope: { id }` | `Scope = "id"` (serial, start order, `IsPaused` while waiting) |
| `mutationKey` | `MutationKey` |
| Paused offline mutations, `resumePausedMutations` | `NetworkMode.Online` (waits in memory), `PersistentMutationQueue` (durable, at-least-once, dead letters) |
| `mutationCache.subscribe` | `Cache.Mutations.Changed` |

## Differences worth knowing

- **Removing an observed query** resets and refetches it rather than dropping it, so components never end up
  observing a dead entry.
- **Invalidation cancels an outdated in-flight fetch** and hands its waiters the fresh result; background
  triggers (focus, reconnect, interval) deduplicate with it instead.
- **Structural sharing is opt-in** - .NET types define their own `Equals`, and identity-only equality would make
  sharing keep stale instances.
- **Blazor Server isolation** - the client is scoped per circuit; use `ResetOnUserChange()` so a WebAssembly
  tab forgets the previous user's data on sign-out.
- **Threading** - fetchers run on the thread pool; observers are notified in order on the thread that produced
  the change, and the Blazor and MVVM adapters marshal to the UI.
