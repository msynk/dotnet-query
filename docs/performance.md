# Performance Tips

DotNetQuery is built for enterprise-scale apps: async-first, low-allocation, thread-safe, and AOT/trimming friendly. This guide covers how to keep it fast.

## Let deduplication work for you

Requesting the same key from many places is cheap - concurrent requests collapse into a single fetch. Don't hand-roll your own "is it already loading?" guards; call `FetchAsync` freely.

## Tune stale time to cut network traffic

A non-zero `StaleTime` is the single biggest lever. Fresh data is served straight from cache with no fetch and no allocation beyond the result view.

```csharp
options.DefaultStaleTime = TimeSpan.FromMinutes(5);
```

Use short stale times for volatile data, long ones for reference data.

## Keep queries observed only while visible

An entry with no observers becomes eligible for garbage collection after its `CacheTime`. Dispose observers (`QueryObserver`, `ObservableQuery`, the `<Query>` component) when the UI goes away so inactive data is reclaimed. Background refetch timers also stop when the last observer leaves.

## Batch invalidations

Prefer one prefix invalidation over many exact ones:

```csharp
await client.InvalidateQueriesAsync(QueryKey.Create("users")); // one call, whole hierarchy
```

## Choose retry policies deliberately

Retries multiply load on a struggling origin. Exponential back-off with jitter (the default) spreads retries and avoids thundering herds:

```csharp
options.DefaultRetryDelay = RetryPolicy.ExponentialWithJitter(TimeSpan.FromSeconds(1));
```

Set `RetryCount = 0` for operations that shouldn't retry (e.g. non-idempotent reads behind a mutation).

## Immutable state, minimal notifications

`QueryState<T>` is an immutable record updated with `with`. Observers are notified only for their own query, and notifications carry a single snapshot - there's no diffing storm. Keep your `StateChanged` handlers light and marshal to the UI thread once (the MVVM and Blazor adapters already do this).

## AOT and trimming

`DotNetQuery.Core` is annotated `IsAotCompatible` and uses no reflection, no dynamic code, and nullable reference types throughout. It is safe under Native AOT and trimming. The `Persistence` and `DevTools` packages use reflection-based `System.Text.Json` and therefore opt out of the AOT guarantee - supply a `JsonSerializerContext` if you need AOT-safe serialization there.

## Time is injectable

`QueryClient` takes a `TimeProvider`. In tests, pass `FakeTimeProvider` to make staleness, retries, background refresh, and GC fully deterministic without real delays.
