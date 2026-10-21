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
