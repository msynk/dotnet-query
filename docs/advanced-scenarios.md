# Advanced Scenarios

Recipes for persistence, resource control, and testing beyond the basics.

## Persisting the cache across sessions

`QueryPersistenceManager` bridges an `IQueryClient` and an `IQueryPersister`. Register the queries you
want persisted (this captures the data type in a closure - no reflection over open generics), restore on
startup, and either persist explicitly or let auto-persist save changes as they happen.

```csharp
var manager = new QueryPersistenceManager(client, persister, new QueryPersistenceOptions
    {
        MaxAge = TimeSpan.FromDays(1),   // discard (and delete) older entries on restore
        Buster = "2026-10",              // ignore data persisted by an incompatible version
    })
    .Register(TodoQueries.List(api), AppJson.Default.ListTodo)        // trim/AOT-safe
    .Register<User>(QueryKey.Create<User>("me"));                      // reflection-based

manager.Error += (key, error) => logger.LogWarning(error, "Persistence failed for {Key}", key);

await manager.RestoreAllAsync();   // on startup
manager.StartAutoPersist();        // save registered queries whenever their data changes (debounced)
// ... or persist explicitly:
await manager.PersistAllAsync();
```

- Restored data keeps its original timestamp (it is hydrated, never treated as freshly fetched), and is
  applied only if the client does not already hold newer data.
- A payload that no longer deserializes (a schema change) is reported via `Error` and deleted, instead of
  failing startup. Writes for the same key are serialized, so an older snapshot can never overwrite a newer
  one.

### Schema changes and migrations

Bump `Buster` when the shape of persisted data changes, and either let old data be discarded or migrate it:

```csharp
new QueryPersistenceOptions
{
    Buster = "v3",
    Migrations = new Dictionary<string, Func<string, string?>>
    {
        ["v2"] = payload => payload.Replace("\"name\":", "\"title\":"),   // rewrite, or return null to drop
        [""] = payload => payload,                                         // data persisted before busters existed
    },
}
```

Migrated records are re-saved under the current buster and the old ones deleted during `RestoreAllAsync`.

### Dynamic keys

Queries with per-id keys (`["todo", 7]`) are registered by prefix. A key cannot be rebuilt from its persisted
text, so `RestoreAllAsync` applies prefixed records to matching queries already in the cache and keeps the rest
ready: the moment the app first fetches or watches such a query, its record is hydrated synchronously - before
the fetch decides whether the data is fresh, so fresh records are served without a request.

```csharp
manager.RegisterPrefix(QueryKey.Create("todo"), AppJson.Default.Todo);   // every Todo query under ["todo", ...]
```

Exact registrations win over prefixes; only queries of the registered type are persisted (another type under
the same prefix is skipped).

### Encrypting persisted data

Wrap any persister in `ProtectedQueryPersister` to transform payloads at rest - with ASP.NET Core Data
Protection or any encryption you trust. It also protects the variables of a `PersistentMutationQueue` built on
it. Records that cannot be unprotected (rotated key, tampering, plain data from before) are skipped and
reported through its `Error` event, so a key change degrades to refetching instead of failing.

```csharp
var protector = dataProtection.CreateProtector("DotNetQuery.Cache.v1");
var persister = new ProtectedQueryPersister(new FileSystemQueryPersister(dir), protector.Protect, protector.Unprotect);
```

### Choosing a persister

| Persister | Package | Use for |
| --- | --- | --- |
| `InMemoryQueryPersister` | `Msynk.DotNetQuery` | tests, transient caches |
| `FileSystemQueryPersister` | `Msynk.DotNetQuery` | desktop/mobile, one JSON file per query |
| `SqliteQueryPersister` | `Msynk.DotNetQuery.Persistence.Sqlite` | durable single-file store, many queries |
| `RedisQueryPersister` | `Msynk.DotNetQuery.Persistence.Redis` | shared / server-side caches |
| `LocalStorageQueryPersister` | `Msynk.DotNetQuery.Blazor` | Blazor WebAssembly (survives reloads) |
| `IndexedDbQueryPersister` | `Msynk.DotNetQuery.Blazor` | Blazor WebAssembly, larger datasets (past localStorage's ~5 MB) |

```csharp
await using var persister = SqliteQueryPersister.ForFile("cache.db");
```

`SqliteQueryPersister` and `RedisQueryPersister` hold a connection open for their lifetime - **dispose
them** (`await using`) to release it. The file-system provider writes atomically and skips unreadable files.
Implement your own provider by implementing the five `IQueryPersister` methods over
`PersistedQuery(Key, Payload, UpdatedAt)`; `PersistedQueryJson` serializes the envelope trim/AOT-safely.

For a **durable offline mutation queue** on top of any persister, see `PersistentMutationQueue` in
[Offline Mode](offline-mode.md#durable-offline-queue-survives-restarts).

## Fetching JSON over HTTP

The HTTP extensions turn an `HttpClient` + URL into a fetcher and fetch JSON straight into the cache:

```csharp
// Trim/AOT-safe with source-generated metadata:
var todo = http.JsonQuery(QueryKey.Create<Todo>(7), "todos/7", AppJson.Default.Todo);  // a QueryDefinition<Todo>
await client.FetchAsync(todo);

// Reflection-based conveniences:
await client.FetchJsonAsync<Todo>(QueryKey.Create<Todo>(7), http, "todos/7");
var fetcher = http.JsonFetcher<List<Todo>>("todos");   // reuse anywhere a fetcher is accepted
```

The GraphQL extensions (namespace `DotNetQuery.GraphQL`) do the same for GraphQL endpoints - they POST
`{ query, variables }`, unwrap the `data` envelope, and raise a `GraphQLException` on `errors`:

```csharp
// Trim/AOT-safe: variables as a JsonObject, data via source-generated metadata.
var user = http.GraphQLQuery(QueryKey.Create<UserResponse>(7), "graphql",
    "query($id:Int!){ user(id:$id){ id name } }", AppJson.Default.UserResponse, new JsonObject { ["id"] = 7 });
await client.FetchAsync(user);

// Reflection-based:
await client.FetchGraphQLAsync<UserResponse>(
    QueryKey.Create<UserResponse>(7), http, "graphql",
    "query($id:Int!){ user(id:$id){ id name } }", new { id = 7 });
```

## Real-time updates (SignalR, channels, gRPC streaming)

For server *events* (an entity changed, was created or deleted), use the event synchronizer: typed mappings to
invalidations, incremental updates and entity upserts, with duplicate and ordering protection and recovery after a
reconnect - see [Real-time synchronization](realtime-sync.md) and the `Msynk.DotNetQuery.SignalR` package. The
`StreamIntoCacheAsync` bridge below suits a continuous *stream* of values for one query (a price ticker, a log tail).

`StreamIntoCacheAsync` bridges any `IAsyncEnumerable<T>` into the cache: each emitted value is written
under a key, so observers (the Blazor `<Query>` component, MVVM bindings, …) update live. It pairs
directly with SignalR's `HubConnection.StreamAsync<T>`, a `Channel<T>` reader, or gRPC server streaming -
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

The returned task completes when the stream ends or the token cancels - run it in the background (or tie
it to a component's lifetime) and cancel the token to stop. The accumulate overload is an atomic
read-modify-write, so several streams (or optimistic updates) can write to the same key without losing
items.

## Synchronizing caches (tabs, windows, processes)

`QuerySynchronizer` keeps registered queries consistent between several caches over any `IQuerySyncChannel`
(a transport of opaque string messages): data written in one cache - a fetch, `SetQueryData`, an optimistic
update or its rollback - appears in the others, and invalidations reach every cache.

```csharp
var sync = new QuerySynchronizer(client, channel, new QuerySyncOptions { ChannelName = "v3" })
    .Register(TodoQueries.All, AppJson.Default.ListTodo)              // an exact key
    .RegisterPrefix(QueryKey.Create("todo"), AppJson.Default.Todo)    // every cached query under a prefix
    .Start();
```

- Conflicts resolve by timestamp (last writer wins), so caches converge; the sender of the current value may
  always revise it, which is how a rollback to an older snapshot propagates.
- Prefix registrations update only matching queries the receiving cache already holds (a key cannot be rebuilt
  from its text form).
- Removals are not synchronized: garbage collection removes entries too, and that is local memory management.
- `ChannelName` isolates incompatible participants (use your app or schema version).
- Transports: `InMemoryQuerySyncHub` (several clients in one process, tests), the Blazor `BroadcastChannel`
  transport (`AddCrossTabSync`, see the [Blazor guide](blazor.md)), `RedisQuerySyncChannel` (server instances, below),
  or your own - implement `PostAsync` and raise `MessageReceived` (SignalR recipe below).
- Messages carry serialized data: synchronize only what every participant may see.

### Several server instances (Redis pub/sub)

Behind a load balancer each instance has its own cache; a write handled by one instance leaves the others stale until
their stale time passes. `RedisQuerySyncChannel` (in `Msynk.DotNetQuery.Persistence.Redis`) connects them over a Redis
channel:

```csharp
var channel = await RedisQuerySyncChannel.CreateAsync(multiplexer, "myapp:dnq-sync");   // or ConnectAsync("redis:6379")
var sync = new QuerySynchronizer(client, channel)
    .RegisterPrefix(CatalogQueries.All, AppJson.Default.Product)
    .Start();
```

Pub/sub is fire-and-forget: an instance disconnected while a message is published misses it, so keep a stale time
that bounds how long that can matter. Use one channel name per application and schema version.

### SignalR (recipe)

To keep caches current with *server-side* changes, prefer the event synchronizer (`Msynk.DotNetQuery.SignalR`, see
[Real-time synchronization](realtime-sync.md)). The recipe below instead relays `QuerySynchronizer` messages - cached
data written by one client - between browsers or apps through your own server: implement the channel over a hub
connection and broadcast on the server:

```csharp
// Client (WebAssembly, MAUI, WPF ...): Microsoft.AspNetCore.SignalR.Client
public sealed class SignalRQuerySyncChannel : IQuerySyncChannel
{
    private readonly HubConnection _hub;

    public SignalRQuerySyncChannel(HubConnection hub)
    {
        _hub = hub;
        _hub.On<string>("QuerySync", message => MessageReceived?.Invoke(message));
    }

    public event Action<string>? MessageReceived;

    public Task PostAsync(string message, CancellationToken cancellationToken = default) =>
        _hub.State == HubConnectionState.Connected ? _hub.SendAsync("Publish", message, cancellationToken) : Task.CompletedTask;
}

// Server: relay to everyone else (scope it with groups, for example per tenant)
public sealed class QuerySyncHub : Hub
{
    public Task Publish(string message) => Clients.Others.SendAsync("QuerySync", message);
}
```

Authorize the hub like any other endpoint: messages carry query data.

## Building keys safely

Derive hierarchical keys from a parent with `Append`, or compose them with the fluent builder - so a
segment name lives in exactly one place:

```csharp
var users  = QueryKey.Create("users");     // ["users"]
var user42 = users.Append(42);             // ["users", 42] - users.IsPrefixOf(user42) is true
var built  = QueryKey.Builder("users").Add(42).Add("posts").Build();
```

## Cancelling work for queries nobody is watching

By default an in-flight fetch runs to completion even after its last observer unsubscribes, so the result
still warms the cache. When a query is expensive and a result nobody asked for is wasteful, opt into
cancellation - the shared fetch is aborted the moment the last observer leaves (for example, a component
disposed mid-fetch). The previous state is restored; showing the component again starts a fresh fetch.

```csharp
// Globally:
services.AddDotNetQuery(o => o.DefaultCancelInFlightWhenUnobserved = true);

// Or per query:
await client.FetchAsync(key, fetcher, new QueryOptions { CancelInFlightWhenUnobserved = true });
```

The fetcher must honor its `CancellationToken` for cancellation to actually stop the work.

## Testing your queries

The core takes a `TimeProvider`, so staleness, retries, and background refresh are fully deterministic
under `Microsoft.Extensions.TimeProvider.Testing`'s `FakeTimeProvider` - advance time explicitly instead
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
- Starting a fetch moves the query to `Loading` synchronously, so assertions right after calling
  `FetchAsync` (without awaiting) see it; bind first-load spinners to `IsPending`.
- Use a `QueryObserver<T>` (or the `<Query>` component in bUnit) to assert on the sequence of emitted
  states, and `IQueryClient.DiagnosticEvent` to assert that a fetch actually happened.
- Drive connectivity, focus, and resume in tests via `ConnectivityProvider.SetOnline(...)` and the signal
  managers, rather than waiting on real platform events.
