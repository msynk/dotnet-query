# Troubleshooting

Symptoms, their usual causes, and how to confirm them. The DevTools panel (Blazor), `QueryDevTools` (any app) and
`client.DiagnosticEvent` show what the cache is doing; most questions below are answered by a query's timeline.

## Data and fetching

**A query fetches on every render or navigation.**
The data is stale immediately: the default `StaleTime` is zero. Set `DefaultStaleTime` (or a per-query
`StaleTime`) to how long the data may be shown without asking the server. A fresh query is served from the cache
without calling the fetcher.

**Two components show different data for what should be the same query.**
They use different keys (`["todo", 5]` vs `["todos", 5]`, or an `int` vs a `long` segment). Define the key once - in
a `QueryDefinition<T>` or with `[GenerateQueries]` - and reuse it.

**The fetcher runs twice for one change.**
Two refetches were requested for different reasons (for example a mutation's `Invalidates` plus a manual
`RefetchQueryAsync`). Concurrent requests for one key share one fetch; sequential ones do not. Check the query's
timeline: the invalidation or mutation recorded just before each fetch shows what caused it.

**"Query X was previously used with a different data type."**
One key maps to one data type. Give queries with different result types different keys (for example append a
`"summary"` segment).

**A query never refetches after a mutation.**
`Invalidates` lists a key that does not match: invalidation matches by prefix, so `["todos"]` covers
`["todos", "list", "open"]`, but `["todo"]` does not. Generated mutations (`[GenerateQueries]`) invalidate the
whole API by default.

**Stale data after the app comes back online / to the foreground.**
Signals must be wired: `AddDotNetQueryBlazor()` does it in Blazor; desktop and mobile apps need their lifecycle
class (`WpfQueryLifecycle`, `WinUIQueryLifecycle`, `AvaloniaQueryLifecycle`, `MauiQueryLifecycle`). Only active,
stale queries refetch, and only when `RefetchOnFocus` / `RefetchOnResume` / `RefetchOnReconnect` are on.

**A query stays `Paused`.**
The client believes it is offline (`ConnectivityProvider.IsOnline` is false) and the query uses
`NetworkMode.Online`. Check what drives connectivity; use `NetworkMode.Always` for requests that work without the
network (local files, an in-process service).

**A fetch hangs.**
Set `QueryOptions.Timeout` - an attempt that exceeds it fails with `TimeoutException` and is retried like any
failure. `MaxConcurrentFetches` queues fetches beyond the limit; a queue that never drains means a fetcher never
completes.

## Mutations and optimistic updates

**The optimistic value flickers back and forth.**
Refreshes of a query wait while declarative `OptimisticUpdates` on it are pending, so this happens with writes the
engine does not know are optimistic: an `OnMutate` callback writing with `SetQueryData`. Prefer `OptimisticUpdates`,
and add the key to `Invalidates` so the server's value replaces the optimistic one once the mutation settles.

**A rolled-back mutation also undid another mutation's change.**
That cannot happen with declarative `OptimisticUpdates` (layers are re-applied on rollback). It can when an
`OnMutate` callback writes with `SetQueryData` and restores a captured value in `OnError`; prefer `OptimisticUpdates`.

**A queued write reaches the server twice (several tabs or windows).**
Each instance that restores the same durable queue store replays it. Set `PersistentMutationQueueOptions.ReplayLock`
(`WebLocksMutationQueueLock` in the browser, `FileMutationQueueLock` for processes) - see
[Offline mode](offline-mode.md#several-tabs-or-processes-sharing-the-queue) - and send the mutation id as an idempotency
key.

**A refetch I requested did not happen right away.**
Optimistic updates of pending mutations are on that query: its refreshes wait until they settle, then one refetch runs
(see [Concurrent mutations of one list](optimistic-updates.md#concurrent-mutations-of-one-list)). The query is marked
stale meanwhile.

**Double submit.**
Bind buttons to `ObservableMutation.MutateCommand` (disabled while pending) or disable them on `IsPending`; set
`MutationOptions.DeduplicatePending` to share a pending run for identical variables.

## Blazor

**The list flashes "Loading…" after prerendering, or the API is called twice.**
The prerendered state was not hydrated: the key differs between the server render and the interactive render
(for example it includes a value only available in the browser), or the component was not rendered by `<Query>` /
`DotNetQueryComponentBase`. Keys must be identical in both renders.

**Users see each other's data (Blazor Server).**
The client must be scoped (`AddDotNetQueryBlazor()` registers it scoped). Never register `IQueryClient` as a
singleton in a server app with per-user data. In WebAssembly, call `.ResetOnUserChange()` so a sign-out clears the
tab's cache.

**Clicks right after the page loads are lost.**
The page was not interactive yet. Disable inputs until `RendererInfo.IsInteractive` (see
[Render modes](blazor.md#render-modes)).

**Prerendered data is refetched in a trimmed or NativeAOT WebAssembly app.**
Hydration could not (de)serialize the type without reflection. Register your `JsonSerializerContext`:
`AddDotNetQueryBlazor().UseHydrationJson(AppJson.Default)`, in both the server and the `.Client` project.

**Stale data after a Blazor Server connection drop.**
Reference `Msynk.DotNetQuery.Blazor.Server` and call `.RefetchOnCircuitReconnect()` in the server project.

**`JSDisconnectedException` in the logs on shutdown.**
The circuit closed while a browser call was in flight; the adapter's components ignore it. If it comes from your
own JS interop, catch it in `DisposeAsync`.

## Desktop and mobile

**"Create DotNetQuery MVVM bindings on the UI thread…"**
The factories capture the UI thread's `SynchronizationContext`. Create view models on the UI thread (or create the
binding there and pass it in).

**How do I see what the cache is doing in a desktop app?**
Start a `QueryDevToolsServer` in debug builds and run the CLI's `watch` mode - see
[Diagnostics](diagnostics.md#live-devtools-for-any-app).

**Bound values update but the list control loses its scroll position.**
Something replaces the data instance on every refetch. Turn on `StructuralSharing` so an equal refetch keeps the
same instance; `ObservableQuery` raises `Data` only when the instance changes.

## Real-time synchronization

**Server events do not reach the cache, or stop after a reconnect.** Check `synchronizer.GetStatistics()` (mappings,
counters, recent errors) and the `QuerySyncEventProcessedEvent` outcomes; rejoin SignalR groups in
`HubConnectionQueryEventSourceOptions.OnConnected`, since memberships do not survive a reconnect. The
[real-time guide](realtime-sync.md#troubleshooting) has a symptom table.

## Logging

**Nothing is logged.** `AddDiagnosticLogging()` needs an `ILoggerFactory` in the container (the generic host and
ASP.NET Core register one) and the `DotNetQuery` category enabled at the level you want - routine events are `Debug`.

## Persistence

**Nothing is restored after a restart.**
Queries must be registered before `RestoreAllAsync` (exact keys with `Register`, families with `RegisterPrefix`),
the `Buster` must match the one used when saving (or a `Migrations` entry must handle the old one), and `MaxAge`
must not have expired.

**A trimmed / NativeAOT app throws when persisting.**
Use the `JsonTypeInfo<T>` overloads with a source-generated `JsonSerializerContext`; the reflection overloads are
annotated as not trim-safe.

## Build

**`DNQ001` / `DNQ002` / `DNQ003` warnings.**
From the `[GenerateQueries]` generator: an annotated method that cannot be generated (for example an
`[InfiniteQueryMethod]` naming a parameter that does not exist), an `Invalidates` name that is not a query method, or a
target that is not an accessible, non-generic interface or non-static class. See
[Generated queries](source-generation.md).

**The package `DotNetQuery.Core` / `DotNetQuery.Blazor` is a different library.**
This project's packages are `Msynk.DotNetQuery*` (see [Migrating between releases](migration.md)).
