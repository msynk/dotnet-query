# Performance Tips

DotNetQuery is built for large apps: async-first, low-allocation, thread-safe, and trimming/NativeAOT
friendly. This guide covers how to keep it fast.

## Let deduplication work for you

Requesting the same key from many places is cheap - concurrent requests collapse into a single fetch.
Don't hand-roll "is it already loading?" guards; call `FetchAsync` (or render `<Query>`) freely.

## Tune stale time to cut network traffic

A non-zero `StaleTime` is the single biggest lever. Fresh data is served straight from the cache with no
fetch.

```csharp
options.DefaultStaleTime = TimeSpan.FromMinutes(5);
```

Use short stale times for volatile data, long ones (or `Timeout.InfiniteTimeSpan` + invalidation) for
reference data. For Blazor prerendering, a non-zero stale time also means hydrated data is used without
revalidating.

## Keep queries observed only while visible

An entry with no observers becomes eligible for garbage collection after its `CacheTime`. Dispose observers
(`QueryObserver`, `ObservableQuery`, `InfiniteQuery`; Blazor components do it for you) when the UI goes
away. Refetch-interval timers stop when the last observer leaves. Opt into
`CancelInFlightWhenUnobserved` for expensive queries whose results are not worth keeping.

## Batch invalidations

Prefer one prefix invalidation over many exact ones:

```csharp
await client.InvalidateQueriesAsync(QueryKey.Create("users")); // one call, whole hierarchy
```

## Choose retry policies deliberately

Retries multiply load on a struggling origin. Exponential back-off with jitter (the default) spreads retries
and avoids thundering herds. Set `RetryCount = 0` for requests that shouldn't retry. Retries stop
automatically when the device goes offline.

Not every failure is worth retrying: a 401, 403 or 404 will fail the same way every time. `ShouldRetry`
(per query, per mutation, or `DefaultShouldRetry` on the client) vetoes retries for permanent errors - it is
consulted only while the `RetryCount` budget lasts, so it can stop retries but never add more.
`HttpRetryPolicy` ships a ready-made classification and honors the server's `Retry-After` header (the HTTP
and GraphQL fetchers record it on the thrown `HttpRequestException`):

```csharp
builder.Services.AddDotNetQuery(options =>
{
    options.DefaultShouldRetry = HttpRetryPolicy.ShouldRetry;        // network errors, 408, 429, 5xx (not 501/505)
    options.DefaultRetryDelay = HttpRetryPolicy.RespectRetryAfter(); // Retry-After when present, else back-off
});
```

## Shaping network load

```csharp
builder.Services.AddDotNetQuery(o =>
{
    o.MaxConcurrentFetches = 6;                   // at most 6 attempts in flight; others wait by Priority, then in order
    o.FetchRateLimiter = new RateLimiterAdapter(  // any IFetchRateLimiter, e.g. over System.Threading.RateLimiting
        new TokenBucketRateLimiter(new() { TokenLimit = 20, TokensPerPeriod = 20, ReplenishmentPeriod = TimeSpan.FromSeconds(1), QueueLimit = 100 }));
    o.Interceptors.Add(new LoggingInterceptor(logger)); // around every attempt, retries included
});

new QueryOptions
{
    Priority = 10,                                // above-the-fold data first
    Timeout = TimeSpan.FromSeconds(10),           // per attempt; a TimeoutException is retried like any failure
    AdaptiveRefetchInterval = q => q.Data is Job { Done: false } ? TimeSpan.FromSeconds(1) : null, // poll while running
};
```

- A cancelled or superseded query gives up its place in the limiter queue.
- A rejected rate-limit permit fails the attempt with `QueryRateLimitedException` (put a retry-after hint in its
  `Data` and `HttpRetryPolicy.RespectRetryAfter` waits for it). The adapter for a `RateLimiter` is a few lines -
  see the `IFetchRateLimiter` docs; the core takes no dependency on `System.Threading.RateLimiting`.
- `FetchAllAsync(definitions)` fetches a dynamic list of queries in parallel and keeps their order.

## Bounding memory

Time-based garbage collection (`CacheTime`) can be combined with a size bound and memory-pressure trimming:

```csharp
builder.Services.AddDotNetQuery(o =>
{
    o.MaxInactiveQueries = 500;     // evict least recently used inactive queries beyond 500
    o.TrimOnMemoryPressure = true;  // evict every inactive query when memory load is high after a full GC
});
client.Cache.Trim(100);             // or trim on demand
```

Queries that are observed, fetching, or holding a fetch deferred while offline are never evicted.

## Avoid re-downloading unchanged data

Pair refetches with HTTP validators: `ConditionalRequestHandler` (see the HTTP fetcher README) makes repeat GETs
conditional and replays the remembered body on `304 Not Modified`. With `StructuralSharing` enabled as well,
an unchanged response also keeps the cached instance, so observers and bindings see no data change at all.

## Measuring

`tests/DotNetQuery.Benchmarks` covers key hashing, cache hits, cold fetches, deduplication, notification
fan-out, selectors, structural sharing, transactions, cache footprint, large updates, infinite-query memory,
persistence throughput, eviction, Blazor rendering, and a comparison with `MemoryCache`. The committed baselines are
in `tests/DotNetQuery.Benchmarks/Baselines/`. Indicative numbers from the latest (ShortRun, .NET 10, i9-14900K):

| Operation | Time | Allocated |
| --- | --- | --- |
| Fresh cache hit (`FetchAsync` served from the cache) | ~120 ns | 104 B (32 B without the caller's own `await`) |
| `MemoryCache.GetOrCreateAsync` hit, for comparison | ~50 ns | 144 B |
| `GetQueryData` | - | 0 B |
| `SetQueryData` notifying 1 / 100 observers | ~0.15 / ~2.4 µs | 152 B / 4 KB |
| Replacing a 10,000-item list observed by 100 observers | ~12 µs | 40 KB |
| One transaction updating 100 keys | ~25 µs | 31 KB |
| Persisting / restoring 100 queries | ~19 / ~19 µs | 16 / 67 KB |
| Collecting 10,000 expired queries | ~1.5 ms | 120 B |
| A cache write re-rendering a mounted Blazor `<Query>` | ~11 µs | 12 KB |

A DotNetQuery hit costs more than `MemoryCache` because it also resolves the query's options, checks staleness,
and records the fetcher that invalidation and refetch signals reuse. The allocation of the hottest paths is guarded
by `AllocationBudgetTests`, which fail the build when a change makes them allocate noticeably more.

## Notifications

`QueryState<T>` is an immutable record. Observers are notified only for their own query, every transition
is delivered once and in order, and identical consecutive states are not re-announced. Keep `StateChanged`
handlers light; they run on the thread that changed the state (the MVVM and Blazor adapters marshal to the
UI thread). `SetQueryData` updaters may be re-run under contention - keep them pure and fast.

## Trimming and NativeAOT

`DotNetQuery`, `DotNetQuery.Persistence.Sqlite` and `DotNetQuery.Persistence.Redis` are
`IsAotCompatible` and analyzed for trim/AOT safety; CI publishes a NativeAOT harness with warnings as errors.
The convenience overloads that serialize your types by reflection are annotated with
`[RequiresUnreferencedCode]`; use the `JsonTypeInfo<T>` overloads with a source-generated
`JsonSerializerContext` in trimmed apps (Blazor WebAssembly) and NativeAOT:

```csharp
[JsonSerializable(typeof(List<Todo>))]
internal partial class AppJson : JsonSerializerContext;

var todos = http.JsonQuery(TodoQueries.All, "todos", AppJson.Default.ListTodo);
persistence.Register(todos, AppJson.Default.ListTodo);
```

The DevTools previews and export (`QueryDevTools.PreviewData`/`Preview`/`ExportJson`) use reflection-based
JSON, are annotated accordingly, and are meant for development.

## Time is injectable

`QueryClient` takes a `TimeProvider`. In tests, pass `FakeTimeProvider` to make staleness, retries,
background refresh, and GC fully deterministic without real delays.
