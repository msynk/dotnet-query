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
A refetch of the same key landed while the mutation was pending. Add the key to the mutation's `Invalidates` so the
server's value replaces the optimistic one after the mutation settles, and avoid manual refetches of that key while
it is pending.

**A rolled-back mutation also undid another mutation's change.**
That cannot happen with declarative `OptimisticUpdates` (layers are re-applied on rollback). It can when an
`OnMutate` callback writes with `SetQueryData` and restores a captured value in `OnError`; prefer `OptimisticUpdates`.

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

**`JSDisconnectedException` in the logs on shutdown.**
The circuit closed while a browser call was in flight; the adapter's components ignore it. If it comes from your
own JS interop, catch it in `DisposeAsync`.

## Desktop and mobile

**"Create DotNetQuery MVVM bindings on the UI thread…"**
The factories capture the UI thread's `SynchronizationContext`. Create view models on the UI thread (or create the
binding there and pass it in).

**Bound values update but the list control loses its scroll position.**
Something replaces the data instance on every refetch. Turn on `StructuralSharing` so an equal refetch keeps the
same instance; `ObservableQuery` raises `Data` only when the instance changes.

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
From the `[GenerateQueries]` generator: an annotated method that cannot be generated, an `Invalidates` name that is
not a query method, or a target that is not an accessible, non-generic interface. See
[Generated queries](source-generation.md).

**The package `DotNetQuery.Core` / `DotNetQuery.Blazor` is a different library.**
This project's packages are `Msynk.DotNetQuery*` (see [Migrating between releases](migration.md)).
