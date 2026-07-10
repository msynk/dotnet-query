# Getting Started

## Install

```bash
dotnet add package DotNetQuery.Core
dotnet add package DotNetQuery.DependencyInjection
```

## Register the client

`QueryClient` is designed to be a single instance for your whole application.

```csharp
builder.Services.AddDotNetQuery(options =>
{
    options.DefaultStaleTime = TimeSpan.FromMinutes(5);
    options.DefaultRetryCount = 3;
});
```

Prefer not to use DI? Construct it directly:

```csharp
using var client = new QueryClient(new QueryClientOptions
{
    DefaultStaleTime = TimeSpan.FromMinutes(5),
});
```

## Your first query

A query is identified by a **key** and backed by a **fetcher** — any delegate that returns a `Task<T>`.

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

`FetchAsync` returns a `QueryResult<T>` — an immutable view with `Data`, `Error`, `Status`, and helpers such as `IsLoading`, `IsFetching`, `IsSuccess`, `IsError`. Call `result.EnsureData()` to get the data or throw.

> **Tip:** for a first-load spinner, prefer `IsPending` (true while idle *or* loading) over `IsLoading` — it's stable across the brief `Idle → Loading` transition, so the spinner shows on the very first render.

## Query keys

Keys are immutable, hierarchical, and compared structurally.

```csharp
QueryKey.Create("users");            // ["users"]
QueryKey.Create("users", userId);    // ["users", 42]
QueryKey.Create<User>("profile", id) // ["User", "profile", 42]
```

A broad key is a *prefix* of more specific keys, which powers partial invalidation (see [Caching](caching.md)).

## Reacting to changes

To keep a UI in sync, observe a query. The observer receives an immediate snapshot and every subsequent change; dispose it to unsubscribe.

```csharp
using var observer = client.Watch<List<User>>(QueryKey.Create("users"));
observer.StateChanged += state => Render(state);
```

In Blazor, use the [`<Query>` component](../README.md#blazor-query-component); in WPF/WinUI/Avalonia, use [`ObservableQuery<T>`](../README.md).

## Next

- [Caching](caching.md) — control freshness and invalidation
- [Mutations](mutations.md) — write data back
