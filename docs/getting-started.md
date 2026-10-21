# Getting Started

## Install

```bash
dotnet add package DotNetQuery
dotnet add package DotNetQuery.Blazor      # Blazor apps
```

## Register the client

```csharp
// Desktop, mobile, console, Blazor WebAssembly: one cache for the app.
builder.Services.AddDotNetQuery(options =>
{
    options.DefaultStaleTime = TimeSpan.FromMinutes(5);
    options.DefaultRetryCount = 3;
});

// Blazor (any hosting model): a scoped client - one private cache per user circuit on the server.
builder.Services.AddDotNetQueryBlazor(options => options.DefaultStaleTime = TimeSpan.FromSeconds(30));

// Other multi-user server hosts: choose the lifetime explicitly.
builder.Services.AddDotNetQuery(ServiceLifetime.Scoped, options => { /* ... */ });
```

Prefer not to use DI? Construct it directly (and dispose it when done):

```csharp
using var client = new QueryClient(new QueryClientOptions
{
    DefaultStaleTime = TimeSpan.FromMinutes(5),
});
```

## Your first query

A query is identified by a **key** and backed by a **fetcher** - any delegate that returns a `Task<T>`.

```csharp
var result = await client.FetchAsync(
    QueryKey.Create("users"),
    ct => api.GetUsersAsync(ct));

if (result.IsSuccess)
{
    foreach (var user in result.Data!)
    {
        Console.WriteLine(user.Name);
    }
}
else if (result.IsError)
{
    Console.WriteLine(result.Error!.Message);
}
```

`FetchAsync` returns a `QueryResult<T>` - an immutable view with `Data`, `Error`, `Status`,
`DataUpdatedAt` and helpers such as `IsPending`, `IsLoading`, `IsFetching`, `IsRefetching`, `IsSuccess`,
`IsError`, `IsLoadingError`, `IsRefetchError` and `IsPaused`. Call `result.EnsureData()` to get the data or
throw.

> **Tip:** bind first-load spinners to `IsPending` (no data and no error yet). A fetch moves the query to
> `Loading` synchronously, so the very first render after starting a fetch already shows it.

## Define queries once: `QueryDefinition<T>`

Pairing the same key with the same data type and fetcher at every call site is error-prone. Describe each
query once and pass the definition around:

```csharp
public static class UserQueries
{
    public static readonly QueryKey All = QueryKey.Create("users");

    public static QueryDefinition<List<User>> List(UserApi api) =>
        new(All.Append("list"), api.GetUsersAsync);

    public static QueryDefinition<User> ById(UserApi api, int id) =>
        new(All.Append(id), ct => api.GetUserAsync(id, ct), new QueryOptions { StaleTime = TimeSpan.FromMinutes(1) });
}

var users = await client.FetchAsync(UserQueries.List(api));
var user  = await client.EnsureQueryDataAsync(UserQueries.ById(api, 7)); // cached data, or fetch-and-throw
client.SetQueryData(UserQueries.ById(api, 7), edited);                    // type-checked against User
await client.InvalidateQueriesAsync(UserQueries.All);                     // refreshes every user query
```

Every client operation has a definition overload (`FetchAsync`, `PrefetchAsync`, `EnsureQueryDataAsync`,
`GetQueryData`, `SetQueryData`, `Watch`, `InvalidateQueriesAsync`, `CancelQueriesAsync`,
`RefetchQueryAsync`), as do the Blazor `<Query>` component, `UseQuery`, and the MVVM bindings.
`WithOptions(o => o with { Enabled = ... })` derives a variant.

## Query keys

Keys are immutable, hierarchical, and compared structurally.

```csharp
QueryKey.Create("users");                  // ["users"]
QueryKey.Create("users", userId);          // ["users", 42]
QueryKey.Create<User>("profile", id);      // ["User", "profile", 42]
QueryKey.Create<List<Todo>>();             // ["List<Todo>"] - generic arguments included
QueryKey.Create("todos", new[] { 1, 2 });  // list segments compare element by element
users.Append(42);                          // child key; users.IsPrefixOf(child) == true
```

Use immutable, value-equal segments: strings, numbers, enums, `Guid`s, dates, records, tuples, or lists of
those. `ToString()` is culture-invariant and escapes strings, so it is safe as a persistence/hydration key.
A broad key is a *prefix* of more specific keys, which powers partial invalidation (see
[Caching](caching.md)).

## Reacting to changes

To keep a UI in sync, observe a query. The observer starts with the current snapshot and then receives
every change, in order; dispose it to unsubscribe.

```csharp
using var observer = client.Watch(UserQueries.List(api));
observer.StateChanged += state => Render(state);
await client.FetchAsync(UserQueries.List(api));
```

In Blazor, use the [`<Query>` component or `UseQuery`](blazor.md); in WPF/WinUI/Avalonia/MAUI, use
`ObservableQuery<T>` (namespace `DotNetQuery.Mvvm`) through the platform adapter.

## Next

- [Blazor](blazor.md) - components, hydration, DevTools
- [Caching](caching.md) - control freshness and invalidation
- [Mutations](mutations.md) - write data back
