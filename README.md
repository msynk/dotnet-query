# DotNetQuery

Server-state management for .NET - the native, idiomatic counterpart to TanStack Query, with first-class
Blazor support. DotNetQuery replaces the hand-rolled mix of `HttpClient` calls, component fields, caches and
refresh logic with one consistent layer: fetching, caching, deduplication, invalidation, retries,
optimistic mutations, pagination, prefetching, prerender hydration, persistence, offline queues, and a
visual inspector - built around `Task`, `CancellationToken`, immutable state, and dependency injection.

> **Status: path to 1.0.** Feature-complete core with a frozen public API (PublicApiAnalyzers baselines),
> 249 tests (unit, concurrency/regression, bUnit component), a verified NativeAOT publish, BenchmarkDotNet
> suite, CI, and seven runnable samples. See the [roadmap](ROADMAP.md) and [changelog](CHANGELOG.md).

## Why

| You need | DotNetQuery gives you |
| --- | --- |
| Typed query keys and reusable query definitions | `QueryKey` (hierarchical, structural) + `QueryDefinition<T>` (key + fetcher + options) |
| Request deduplication and configurable freshness | One in-flight fetch per key; `StaleTime`/`CacheTime` per query or globally |
| Cache invalidation and dependency-aware refresh | Prefix/predicate invalidation that cancels outdated fetches; `Invalidates` on mutations |
| Optimistic mutations with rollback | Declarative `OptimisticUpdates`, exact rollback, automatic reconciliation |
| Retries, cancellation, pagination, infinite queries | Back-off policies, `CancelQueriesAsync`, `KeepPreviousData`, cache-backed infinite queries |
| Prefetching and hydration for server rendering | `PrefetchAsync`/`EnsureQueryDataAsync`, `HydrateQueryData`, automatic Blazor prerender hydration |
| Cache persistence and offline mutation queues | Persisters (file, SQLite, Redis, localStorage, IndexedDB), offline pause/resume, durable mutation queue |
| A visual inspector for cached records and mutation history | DevTools model, Blazor panel (inline/floating) with data previews, CLI dashboard |

## Packages

| Package | Description |
| --- | --- |
| `DotNetQuery.Core` | Framework-agnostic engine. Zero UI dependencies, trim/AOT-safe. |
| `DotNetQuery.DependencyInjection` | `AddDotNetQuery` (singleton or scoped) for `Microsoft.Extensions.DependencyInjection`. |
| `DotNetQuery.Blazor` | `<Query>`, hook-style `UseQuery`/`UseMutation`/`UseInfiniteQuery`, prerender hydration, browser signals. |
| `DotNetQuery.DevTools` / `.DevTools.Blazor` | Cache inspector, mutation history, event log, actions, JSON export; visual Blazor panel. |
| `DotNetQuery.Persistence` | Persist/restore registered queries (in-memory, file system) and the durable `PersistentMutationQueue`. |
| `DotNetQuery.Persistence.Sqlite` / `.Redis` / `.BlazorWasm` | SQLite, Redis, and browser `localStorage`/IndexedDB providers. |
| `DotNetQuery.Http` / `.GraphQL` | HTTP/JSON and GraphQL fetchers and query definitions, with source-generated (AOT-safe) overloads. |
| `DotNetQuery.Mvvm` / `.Wpf` / `.WinUI` / `.Avalonia` / `.Maui` | `INotifyPropertyChanged` bindings and dispatcher-aware factories for XAML apps. |

The portable libraries target **.NET 8 (LTS) and .NET 10**, including the Blazor adapters.

## Quick start (Blazor)

```csharp
// Program.cs - a scoped client: one private cache per user circuit, one per tab in WebAssembly.
builder.Services.AddDotNetQueryBlazor(options => options.DefaultStaleTime = TimeSpan.FromSeconds(30));
```

```csharp
// Define each query once.
public static class TodoQueries
{
    public static readonly QueryKey All = QueryKey.Create("todos");

    public static QueryDefinition<List<Todo>> List(TodoApi api, TodoFilter filter) =>
        new(All.Append(filter), ct => api.GetAllAsync(filter, ct));
}
```

```razor
<DotNetQueryBrowserSignals />   @* refetch on focus / reconnect / tab visible *@

<Query Definition="TodoQueries.List(Api, _filter)" KeepPreviousData="true">
    <Loading><p>Loading…</p></Loading>
    <Error Context="error"><p>@error.Message</p></Error>
    <ChildContent Context="todos">
        <ul>@foreach (var t in todos.Data!) { <li>@t.Title</li> }</ul>
        @if (todos.IsFetching) { <small>refreshing…</small> }
    </ChildContent>
</Query>

<QueryDevToolsPanel Floating="true" />
```

Data fetched while prerendering is embedded in the page and hydrated into the interactive render with its
original timestamp - no second request, no loading flash. See the [Blazor guide](docs/blazor.md).

## Quick start (any .NET app)

```csharp
builder.Services.AddDotNetQuery(options => options.DefaultStaleTime = TimeSpan.FromMinutes(5));

// Fetch - deduplicated, cached, retried.
var todos = await client.FetchAsync(TodoQueries.List(api, TodoFilter.All));

// Observe for reactive UIs (every transition, in order).
using var observer = client.Watch(TodoQueries.List(api, TodoFilter.All));
observer.StateChanged += state => Render(state);

// Mutate with a declarative optimistic update, automatic rollback and reconciliation.
var add = client.CreateMutation<string, Todo>(
    (title, ct) => api.AddAsync(title, ct),
    new MutationOptions<string, Todo>
    {
        OptimisticUpdates =
        [
            OptimisticUpdate.Create<string, List<Todo>>(TodoQueries.List(api, TodoFilter.All).Key,
                (list, title) => [.. list ?? [], new Todo(-1, title, false)]),
        ],
        Invalidates = [TodoQueries.All],
    });
await add.MutateAsync("Buy milk");

// Read and write the cache directly.
client.SetQueryData<List<Todo>>(TodoQueries.List(api, TodoFilter.All).Key, list => list!.Where(t => !t.Done).ToList());
await client.InvalidateQueriesAsync(TodoQueries.All);   // refetches every todo query that is on screen
```

### Infinite queries

```csharp
using var feed = client.CreateInfiniteQuery<int, Page>(
    QueryKey.Create("feed"),
    (offset, ct) => api.GetPageAsync(offset, ct),
    new InfiniteQueryOptions<int, Page>
    {
        InitialPageParam = 0,
        GetNextPageParam = (last, _, _) => last.HasMore ? NextPageParam.Of(last.Offset + 10) : NextPageParam.None<int>(),
    });

await feed.FetchAsync();
while (feed.HasNextPage) await feed.FetchNextPageAsync();
```

Pages live in the cache, so infinite queries are invalidated, persisted, inspected and hydrated like any
other query.

### Offline-first

```csharp
// Queries pause while offline (serving cached data) and resume on reconnect - every refetch path honors it.
connectivity.SetOnline(false);

// Writes made offline survive restarts and replay in order on reconnect.
var queue = new PersistentMutationQueue(store, connectivity)
    .RegisterHandler<NewTodo>("todos/add", (todo, ct) => api.CreateAsync(todo, ct), AppJson.Default.NewTodo);
await queue.RestoreAsync();
await queue.EnqueueAsync("todos/add", new NewTodo("Buy milk"));
```

### Persistence across sessions

```csharp
var persistence = new QueryPersistenceManager(client, new FileSystemQueryPersister(appDataDir),
        new QueryPersistenceOptions { MaxAge = TimeSpan.FromDays(7), Buster = "v2" })
    .Register(TodoQueries.List(api, TodoFilter.All), AppJson.Default.ListTodo);

await persistence.RestoreAllAsync();   // keeps original timestamps: stale data revalidates in the background
persistence.StartAutoPersist();        // saves whenever registered data changes
```

### Transports & real-time

```csharp
var todo = http.JsonQuery(QueryKey.Create<Todo>(7), "todos/7", AppJson.Default.Todo);   // HTTP/JSON, AOT-safe
await client.FetchGraphQLAsync<UserResponse>(key, http, "graphql", "query { me { id name } }");   // GraphQL

// Fold any IAsyncEnumerable (SignalR, channels, gRPC streaming) into the cache, atomically.
_ = client.StreamIntoCacheAsync<List<Message>, Message>(QueryKey.Create("chat", roomId),
        hub.StreamAsync<Message>("Messages", roomId), (messages, m) => [.. messages ?? [], m], ct);
```

### Diagnostics

```csharp
client.DiagnosticEvent += e => { if (e is QueryFailedEvent f) logger.LogWarning(f.Error, "Query {Key} failed", f.Key); };
tracerProviderBuilder.AddSource(DotNetQueryDiagnostics.ActivitySourceName);   // OpenTelemetry traces
meterProviderBuilder.AddMeter(DotNetQueryDiagnostics.MeterName);              // and metrics
```

## Guarantees

- **One fetch per key at a time.** Callers share it; one caller cancelling its wait never cancels shared
  work. Invalidation cancels an outdated in-flight fetch and hands its waiters the fresh result.
- **No stuck states.** A cancelled or superseded fetch restores the previous state; a failing subscriber or
  diagnostics handler can never fail, retry, or block a fetch.
- **Ordered notifications.** Observers see every transition exactly once, in order.
- **Safe writes.** `SetQueryData` updaters are atomic; optimistic rollbacks never clobber newer writes;
  mutation callbacks never re-run the mutation.
- **Multi-user safe.** Scoped registration gives every Blazor Server circuit its own cache.
- **Offline-aware.** Every refetch path pauses offline and resumes on reconnect; deferred work is never
  garbage collected.

## Documentation

Guides live in [`docs/`](docs/README.md): [Getting Started](docs/getting-started.md) ·
[Blazor](docs/blazor.md) · [Caching](docs/caching.md) · [Mutations](docs/mutations.md) ·
[Optimistic Updates](docs/optimistic-updates.md) · [Infinite Queries](docs/infinite-queries.md) ·
[Offline Mode](docs/offline-mode.md) · [Diagnostics](docs/diagnostics.md) ·
[Performance](docs/performance.md) · [Advanced Scenarios](docs/advanced-scenarios.md)

## Samples

| Sample | Shows |
| --- | --- |
| `samples/TodoApp` | Blazor Web App: definitions, prerender hydration, keep-previous-data filters, optimistic mutations, floating DevTools |
| `samples/DotNetQuery.Sample.Chat` | Infinite history pagination + live streaming into the cache |
| `samples/DotNetQuery.Sample.OfflineFirst` | Persist → restart offline → restore → offline queue → reconnect replay |
| `samples/DotNetQuery.Sample.ECommerce` | Optimistic cart updates with rollback |
| `samples/DotNetQuery.Sample.Dashboard` | Deduplication and background refresh |
| `samples/DotNetQuery.Sample.Avalonia` / `.Maui` | MVVM bindings on Avalonia (headless-verified in CI) and MAUI |

Run the Blazor sample with `dotnet run --project samples/TodoApp`. The console samples and the Avalonia
headless check run as CI smoke tests.

## Building & testing

```bash
dotnet build DotNetQuery.slnx
dotnet test DotNetQuery.slnx
```

See [CONTRIBUTING.md](CONTRIBUTING.md) for the public-API baseline workflow, engine concurrency rules,
benchmarks and the NativeAOT harness.

## License

MIT
