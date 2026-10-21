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
