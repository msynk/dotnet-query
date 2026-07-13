# Advanced Scenarios

Recipes for persistence, resource control, and testing beyond the basics.

## Persisting the cache across sessions

`QueryPersistenceManager` bridges an `IQueryClient` and an `IQueryPersister`. Register the query keys
you want persisted (this captures the data type in a closure — no reflection over open generics), then
persist on shutdown and restore on startup. Restored data is written back via `SetQueryData` and treated
as fresh.

```csharp
var manager = new QueryPersistenceManager(client, persister)
    .Register<List<Todo>>(QueryKey.Create("todos"))
    .Register<User>(QueryKey.Create<User>("me"));

await manager.RestoreAllAsync();   // on startup
// ... app runs ...
await manager.PersistAllAsync();   // on shutdown, or periodically
```

### Choosing a persister

| Persister | Package | Use for |
| --- | --- | --- |
| `InMemoryQueryPersister` | `DotNetQuery.Persistence` | tests, transient caches |
| `FileSystemQueryPersister` | `DotNetQuery.Persistence` | desktop/mobile, one JSON file per query |
| `SqliteQueryPersister` | `DotNetQuery.Persistence.Sqlite` | durable single-file store, many queries |
| `RedisQueryPersister` | `DotNetQuery.Persistence.Redis` | shared / server-side caches |
| `LocalStorageQueryPersister` | `DotNetQuery.Persistence.BlazorWasm` | Blazor WebAssembly (survives reloads) |

```csharp
await using var persister = SqliteQueryPersister.ForFile("cache.db");
```

`SqliteQueryPersister` and `RedisQueryPersister` hold a connection open for their lifetime — **dispose
them** (`await using`) to release it. Implement your own provider by implementing the five
`IQueryPersister` methods over `PersistedQuery(Key, Payload, UpdatedAt)`.

## Fetching JSON over HTTP

`DotNetQuery.Http` turns an `HttpClient` + URL into a fetcher and fetches JSON straight into the cache:

```csharp
await client.FetchJsonAsync<Todo>(QueryKey.Create<Todo>(7), http, "todos/7");
var fetcher = http.JsonFetcher<List<Todo>>("todos");   // reuse anywhere a fetcher is accepted
```

`DotNetQuery.GraphQL` does the same for GraphQL endpoints — it POSTs `{ query, variables }`, unwraps the
`data` envelope, and raises a `GraphQLException` on `errors`:

```csharp
await client.FetchGraphQLAsync<UserResponse>(
    QueryKey.Create<UserResponse>(7), http, "graphql",
    "query($id:Int!){ user(id:$id){ id name } }", new { id = 7 });
```

## Real-time updates (SignalR, channels, gRPC streaming)

`StreamIntoCacheAsync` bridges any `IAsyncEnumerable<T>` into the cache: each emitted value is written
under a key, so observers (the Blazor `<Query>` component, MVVM bindings, …) update live. It pairs
directly with SignalR's `HubConnection.StreamAsync<T>`, a `Channel<T>` reader, or gRPC server streaming —
the core stays transport-agnostic.

```csharp
// Replace the cached value with each emission (e.g. a live gauge):
_ = client.StreamIntoCacheAsync(QueryKey.Create("price", "BTC"),
        hub.StreamAsync<decimal>("PriceStream", "BTC"), cancellationToken);

// Or fold each item into a growing list (e.g. a chat feed):
_ = client.StreamIntoCacheAsync<List<Message>, Message>(
        QueryKey.Create("chat", roomId),
        hub.StreamAsync<Message>("Messages", roomId),
        (messages, incoming) => [.. messages ?? [], incoming],
        cancellationToken);
```

The returned task completes when the stream ends or the token cancels — run it in the background (or tie
it to a component's lifetime) and dispose the token to stop.

## Building keys safely

Derive hierarchical keys from a parent with `Append`, or compose them with the fluent builder — so a
segment name lives in exactly one place:

```csharp
var users  = QueryKey.Create("users");     // ["users"]
var user42 = users.Append(42);             // ["users", 42] — users.IsPrefixOf(user42) is true
var built  = QueryKey.Builder("users").Add(42).Add("posts").Build();
```

## Cancelling work for queries nobody is watching

By default an in-flight fetch runs to completion even after its last observer unsubscribes, so the result
still warms the cache. When a query is expensive and a result nobody asked for is wasteful, opt into
cancellation — the shared fetch is aborted the moment the last observer leaves (for example, a component
disposed mid-fetch). The last good cached state is left intact.

```csharp
// Globally:
services.AddDotNetQuery(o => o.DefaultCancelInFlightWhenUnobserved = true);

// Or per query:
await client.FetchAsync(key, fetcher, new QueryOptions { CancelInFlightWhenUnobserved = true });
```

The fetcher must honor its `CancellationToken` for cancellation to actually stop the work.

## Testing your queries

The core takes a `TimeProvider`, so staleness, retries, and background refresh are fully deterministic
under `Microsoft.Extensions.TimeProvider.Testing`'s `FakeTimeProvider` — advance time explicitly instead
of sleeping.

```csharp
var time = new FakeTimeProvider();
using var client = new QueryClient(
    new QueryClientOptions { DefaultStaleTime = TimeSpan.FromMinutes(5), DefaultRetryCount = 0 },
    time);

await client.FetchAsync(key, fetcher);              // fetch #1
time.Advance(TimeSpan.FromMinutes(6));              // data is now stale
await client.RefetchQueryAsync(key);               // fetch #2, deterministically
```

Tips:

- Set `DefaultRetryCount = 0` (and `DefaultRetryDelay = RetryPolicy.Constant(TimeSpan.Zero)`) so failing
  fetches surface immediately without back-off delays.
- Bind first-load spinners to `IsPending`, not `IsLoading`: a brand-new query's first synchronous render
  is `Idle` (the `Loading` transition is deferred one scheduler hop for deduplication correctness).
- Use a `QueryObserver<T>` (or the `<Query>` component in bUnit) to assert on the sequence of emitted
  states, and `IQueryClient.DiagnosticEvent` to assert that a fetch actually happened.
- Drive connectivity, focus, and resume in tests via `ConnectivityProvider.SetOnline(...)` and the signal
  managers, rather than waiting on real platform events.
