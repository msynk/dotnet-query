# DotNetQuery

Server-state management for the .NET ecosystem — the native, idiomatic counterpart to TanStack Query. DotNetQuery makes fetching, caching, synchronizing, and invalidating remote data effortless, with a framework-agnostic core built around `Task`, `CancellationToken`, immutable data, and Dependency Injection.

> **Status: path to 1.0 🚧** — All P1 release-readiness work is done: a BenchmarkDotNet suite, a verified NativeAOT publish, GitHub Actions CI, SourceLink, and a **frozen public API surface** (PublicApiAnalyzers baselines). Breadth has landed too — **SQLite / Redis / Blazor-WASM persistence**, an **HTTP/JSON transport** package, **WinUI and MAUI adapters**, structured query-key builders, and an opt-in cancellation policy. See the [roadmap](#roadmap).

## Packages

| Package | Description |
| --- | --- |
| `DotNetQuery.Core` | Framework-agnostic core. Zero UI dependencies. |
| `DotNetQuery.DependencyInjection` | `AddDotNetQuery` registration for `Microsoft.Extensions.DependencyInjection`. |
| `DotNetQuery.Persistence` | Pluggable cache persistence (in-memory + file-system providers). |
| `DotNetQuery.Persistence.Sqlite` | SQLite cache persistence (Microsoft.Data.Sqlite). |
| `DotNetQuery.Persistence.Redis` | Redis cache persistence (StackExchange.Redis) for shared/server-side caches. |
| `DotNetQuery.Persistence.BlazorWasm` | Browser `localStorage` persistence for Blazor WebAssembly. |
| `DotNetQuery.Http` | HTTP/JSON transport conveniences: `HttpClient` → query fetchers, fetch-JSON-into-cache. |
| `DotNetQuery.DevTools` | Framework-agnostic cache inspection, live event log, manual actions, JSON export. |
| `DotNetQuery.Blazor` | Blazor adapter: the `<Query>` component, component-scoped observers, browser focus/online signals. |
| `DotNetQuery.DevTools.Blazor` | Visual DevTools panel component for Blazor. |
| `DotNetQuery.Mvvm` | Framework-agnostic `INotifyPropertyChanged` bindings (`ObservableQuery`/`ObservableMutation`) for any XAML UI. |
| `DotNetQuery.Wpf` | WPF adapter: dispatcher-aware factories over the MVVM bindings. |
| `DotNetQuery.WinUI` | WinUI 3 / Windows App SDK adapter: dispatcher-aware factories over the MVVM bindings. |
| `DotNetQuery.Avalonia` | Avalonia adapter: cross-platform dispatcher-aware factories over the MVVM bindings. |
| `DotNetQuery.Maui` | .NET MAUI adapter: dispatcher factories + a connectivity bridge for offline pause/resume. |

The **`samples/TodoApp`** Blazor Server app demonstrates the full stack — fetching, caching, mutations, invalidation, and the live DevTools panel — end to end. Run it with `dotnet run --project samples/TodoApp`. The **`samples/DotNetQuery.Sample.Maui`** app demonstrates the MAUI adapter (`ObservableQuery` binding + connectivity bridge) on Android — see its [README](samples/DotNetQuery.Sample.Maui/README.md). The **`samples/DotNetQuery.Sample.Avalonia`** app demonstrates the Avalonia adapter and ships a headless-render self-check (`dotnet run --project samples/DotNetQuery.Sample.Avalonia -- verify`) that runs as an end-to-end smoke test in CI.

## Quick start

```csharp
// 1. Register the client
builder.Services.AddDotNetQuery(options =>
{
    options.DefaultStaleTime = TimeSpan.FromMinutes(5);
    options.DefaultRetryCount = 3;
});

// 2. Fetch — deduplicated, cached, retried
var result = await queryClient.FetchAsync(
    QueryKey.Create("users"),
    ct => api.GetUsersAsync(ct));

if (result.IsSuccess)
{
    foreach (var user in result.Data!) { /* ... */ }
}

// 3. Invalidate a whole key hierarchy
await queryClient.InvalidateQueriesAsync(QueryKey.Create("users"));

// 4. Observe a query for reactive UIs
using var observer = queryClient.Watch<List<User>>(QueryKey.Create("users"));
observer.StateChanged += state => Render(state);

// 5. Mutate with an optimistic update + rollback
await queryClient.MutateAsync<User, User>(
    (user, ct) => api.UpdateUserAsync(user, ct),
    editedUser,
    new MutationOptions<User, User>
    {
        OnMutate = (user, _) =>
        {
            var previous = queryClient.GetQueryData<User>(QueryKey.Create<User>(user.Id)).Data;
            queryClient.SetQueryData(QueryKey.Create<User>(user.Id), user); // optimistic
            return ValueTask.FromResult<object?>(previous);
        },
        OnError = (_, user, ctx, _) =>
        {
            queryClient.SetQueryData(QueryKey.Create<User>(user.Id), (User)ctx!); // rollback
            return ValueTask.CompletedTask;
        },
    });
```

### Infinite (paginated) queries

```csharp
var feed = queryClient.CreateInfiniteQuery<int, Page>(
    QueryKey.Create("feed"),
    (offset, ct) => api.GetPageAsync(offset, ct),
    new InfiniteQueryOptions<int, Page>
    {
        InitialPageParam = 0,
        GetNextPageParam = (last, _, _) =>
            last.HasMore ? NextPageParam.Of(last.Offset + 10) : NextPageParam.None<int>(),
    });

await feed.FetchAsync();
while (feed.HasNextPage)
{
    await feed.FetchNextPageAsync();
}
```

### Persistence across sessions

```csharp
var persister = new FileSystemQueryPersister(appDataDir);
var persistence = new QueryPersistenceManager(queryClient, persister)
    .Register<List<Todo>>(QueryKey.Create("todos"));

await persistence.RestoreAllAsync();   // on startup
await persistence.PersistAllAsync();   // on shutdown / periodically
```

### Diagnostics & OpenTelemetry

```csharp
// In-process event stream
queryClient.DiagnosticEvent += e =>
{
    if (e is QueryFailedEvent f) logger.LogWarning(f.Error, "Query {Key} failed", f.Key);
};

// OpenTelemetry — no extra DotNetQuery dependency; these are BCL ActivitySource/Meter
tracerProviderBuilder.AddSource(DotNetQueryDiagnostics.ActivitySourceName);
meterProviderBuilder.AddMeter(DotNetQueryDiagnostics.MeterName);
```

### Offline support

```csharp
// Adapters push connectivity changes; queries pause when offline and refetch on reconnect.
connectivity.SetOnline(false);
var result = await queryClient.FetchAsync(key, fetcher); // Status == Paused (served cache if any)

// Queue mutations while offline; they replay in order when back online.
var queue = new OfflineMutationQueue(connectivity);
await queue.EnqueueAsync(ct => api.CreateTodoAsync(todo, ct));
```

### Blazor `<Query>` component

```razor
@* Program.cs: builder.Services.AddDotNetQueryBlazor(); *@
<DotNetQueryBrowserSignals />  @* enables focus/reconnect refetching *@

<Query TData="List<Todo>" Key="_key" Fetcher="ct => Api.GetAllAsync(ct)" Context="result">
    @if (result.IsLoading) { <p>Loading…</p> }
    else if (result.IsError) { <p>@result.Error?.Message</p> }
    else
    {
        <ul>@foreach (var t in result.Data!) { <li>@t.Title</li> }</ul>
    }
</Query>

@code { private readonly QueryKey _key = QueryKey.Create("todos"); }
```

### DevTools (framework-agnostic)

```csharp
using var devtools = new QueryDevTools(queryClient);

foreach (var q in devtools.InspectQueries())
    Console.WriteLine($"{q.Key}  {q.Status}  observers={q.ObserverCount}  stale={q.IsStale}");

var stats = devtools.GetStats();          // status distribution, fetch/mutation tallies, avg duration
await devtools.RefetchAsync(key);         // manual actions
string snapshot = devtools.ExportJson();  // export the whole diagnostic state
```

## What v0.1 gives you

- **Query keys** — immutable, hierarchical, structurally-equal `QueryKey` with prefix matching for partial invalidation.
- **Deduplication** — concurrent requests for the same key share a single in-flight fetch; per-caller cancellation never cancels shared work.
- **Caching & staleness** — fresh data is served without a round-trip (`StaleTime`); inactive entries are garbage collected (`CacheTime`).
- **Retries** — constant, exponential, and exponential-with-jitter back-off strategies, or a custom delegate.
- **Invalidation** — mark a key hierarchy stale and automatically refetch active queries.
- **Observers** — subscribe to per-query state changes with immediate snapshots; only affected subscribers are notified.
- **DI-first** — singleton `IQueryClient`, `TimeProvider`-based for fully deterministic tests.
- **AOT/trimming-friendly** — no reflection, nullable reference types throughout, `TreatWarningsAsErrors`.

## Documentation

Full guides live in [`docs/`](docs/README.md):
[Getting Started](docs/getting-started.md) ·
[Caching](docs/caching.md) ·
[Mutations](docs/mutations.md) ·
[Optimistic Updates](docs/optimistic-updates.md) ·
[Infinite Queries](docs/infinite-queries.md) ·
[Offline Mode](docs/offline-mode.md) ·
[Diagnostics](docs/diagnostics.md) ·
[Performance](docs/performance.md) ·
[Advanced Scenarios](docs/advanced-scenarios.md)

## Building & testing

```bash
dotnet build
dotnet test
```

130 tests cover the stack end to end — 121 core (including concurrency/stress tests and
**91% line coverage**) plus 9 bUnit component tests for the Blazor adapter. Run with
coverage via `dotnet test --collect:"XPlat Code Coverage"`. A `DotNetQuery.Benchmarks` (BenchmarkDotNet)
suite and a `DotNetQuery.AotHarness` (verified NativeAOT publish) round out the quality gates; see
[CONTRIBUTING.md](CONTRIBUTING.md).

The portable libraries multi-target **net8.0 and net10.0**, so they run on .NET 8 (LTS) and later; the
platform-specific adapters (WPF, WinUI, MAUI) target their respective frameworks. The libraries are
NuGet-ready — `dotnet pack -c Release` produces `.nupkg` + `.snupkg` (symbol) packages with SourceLink,
embedded README, icon, XML docs, and resolved inter-package dependencies. The public API of every
non-Razor library is locked by committed `PublicAPI.*.txt` baselines.

## Roadmap

See [ROADMAP.md](ROADMAP.md) for the detailed path to 1.0 (remaining work, priorities, and known characteristics).


- **v0.1** ✅ Core architecture, query cache, fetching, DI
- **v0.2** ✅ Mutations + optimistic updates, background refresh, focus/reconnect/resume triggers
- **v0.3** ✅ Infinite queries, persistence providers
- **v0.4** ✅ Offline support, diagnostics, OpenTelemetry
- **v0.5** ✅ DevTools model, cache inspector, diagnostics export
- **v0.6** ✅ Blazor adapter, visual DevTools panel, runnable sample
- **v0.7** ✅ MVVM binding layer, WPF adapter
- **path to 1.0** 🚧 ✅ Benchmarks, NativeAOT, CI, SourceLink, **API freeze**; ✅ SQLite/Redis/Blazor-WASM persistence, HTTP transport, WinUI + MAUI adapters, structured keys, cancellation policy; remaining: more samples, IndexedDB, additional MAUI heads

## License

MIT
