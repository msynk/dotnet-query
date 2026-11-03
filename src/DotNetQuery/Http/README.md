# HTTP fetchers

HTTP/JSON transport conveniences for DotNetQuery, shipped in the `DotNetQuery` package. The engine is
transport-agnostic; these helpers turn an `HttpClient` + URL into a query fetcher and let you fetch JSON
endpoints straight into the cache.

```csharp
using DotNetQuery;

// Build a fetcher from a URL and pass it to the client:
var fetcher = http.JsonFetcher<List<Todo>>("todos");
await client.FetchAsync(QueryKey.Create("todos"), fetcher);

// Or fetch straight into the cache in one call:
var result = await client.FetchJsonAsync<Todo>(QueryKey.Create<Todo>(7), http, "todos/7");

// Warm the cache ahead of navigation (best-effort, errors swallowed):
await client.PrefetchJsonAsync<Todo>(QueryKey.Create<Todo>(7), http, "todos/7");
```

## Behavior

- `JsonFetcher<T>` uses `HttpClient.GetFromJsonAsync<T>`: a non-success status throws
  `HttpRequestException`, and a body that deserializes to `null` throws `InvalidOperationException`
  (a query should resolve to a value, not null).
- Pass a `JsonSerializerOptions` to control naming/converters; the default is `JsonSerializerDefaults.Web`.
- **Trimming / NativeAOT:** every helper has a `JsonTypeInfo<T>` overload (from a source-generated
  `JsonSerializerContext`) that is trim- and AOT-safe - use it in Blazor WebAssembly and AOT apps:
  `http.JsonQuery(key, "todos/7", AppJson.Default.Todo)` returns a ready-to-use `QueryDefinition<Todo>`.
  The `JsonSerializerOptions` overloads use reflection and are annotated `[RequiresUnreferencedCode]`.
- **Retries:** a non-success status throws `HttpRequestException` with `StatusCode` set and any `Retry-After`
  delay recorded (read it with `HttpRetryPolicy.GetRetryAfter`). Use `HttpRetryPolicy.ShouldRetry` to retry
  only transient failures (connection errors, timeouts, 408, 429, 5xx except 501/505) and
  `HttpRetryPolicy.RespectRetryAfter()` to wait as long as the server asks.

## Conditional requests (ETag / Last-Modified)

`ConditionalRequestHandler` is a `DelegatingHandler` that remembers successful GET responses carrying an `ETag`
or `Last-Modified` validator, revalidates the next request for the same URI with `If-None-Match` /
`If-Modified-Since`, and turns a `304 Not Modified` back into the remembered `200` - so refetching unchanged data
costs a round trip but no body transfer, with every fetcher unchanged:

```csharp
services.AddHttpClient<TodoApi>().AddHttpMessageHandler(() => new ConditionalRequestHandler());
```

It skips non-GET requests, requests with their own validators, `Cache-Control: no-store` responses and bodies
above `MaxBodyBytes` (default 4 MiB), and keeps at most `MaxEntries` (default 256, least recently used evicted).
Entries are keyed by URI only: give each user their own handler when responses differ per user, and call
`Clear()` on sign-out. Blazor WebAssembly does not need it - the browser's HTTP cache already revalidates.

## Resilience and authentication handlers

```csharp
services.AddHttpClient<TodoApi>()
    .AddHttpMessageHandler(() => new CircuitBreakerHandler(new() { FailureThreshold = 5, BreakDuration = TimeSpan.FromSeconds(30) }))
    .AddHttpMessageHandler(sp => new BearerTokenHandler(sp.GetRequiredService<Tokens>().GetAsync, sp.GetRequiredService<Tokens>().RefreshAsync))
    .AddHttpMessageHandler(() => new ConditionalRequestHandler());
```

- `CircuitBreakerHandler` opens after consecutive *transient* failures (5xx, 408, 429, network errors - a 404 or
  400 means the server is healthy), fails requests fast with `BrokenCircuitException` (transient, carrying the
  remaining break as `Retry-After`, so `HttpRetryPolicy.RespectRetryAfter` waits for the half-open trial), and
  lets one trial request decide whether to close or re-open. `State`, `StateChanged`, `Reset()`.
- `BearerTokenHandler` adds `Authorization: Bearer <token>`; on `401` it refreshes once - concurrent requests share
  one refresh - and replays the request (body included) with the new token. A failed refresh surfaces the 401, so
  queries never loop on an expired token.

## Batching many single-item queries

`BatchLoader<TKey, TValue>` coalesces the loads issued in the same burst (or within `Delay`) into one batch request,
split by `MaxBatchSize`; each query keeps its own cache entry:

```csharp
var users = new BatchLoader<int, User>((ids, ct) => api.GetUsersAsync(ids, ct)); // IReadOnlyDictionary<int, User>
QueryDefinition<User> UserById(int id) => new(QueryKey.Create("user", id), ct => users.LoadAsync(id, ct));
```
