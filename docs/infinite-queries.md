# Infinite Queries

An infinite query accumulates *pages* of data - for "load more" lists, feeds, and cursor-based APIs. It
supports forward, backward, and bidirectional pagination.

## Creating one

```csharp
using var feed = client.CreateInfiniteQuery<int, Page>(
    QueryKey.Create("feed"),
    (offset, ct) => api.GetPageAsync(offset, ct),
    new InfiniteQueryOptions<int, Page>
    {
        InitialPageParam = 0,
        GetNextPageParam = (lastPage, pages, pageParams) =>
            lastPage.HasMore ? NextPageParam.Of(lastPage.Offset + 10) : NextPageParam.None<int>(),
        QueryOptions = new QueryOptions { StaleTime = TimeSpan.FromMinutes(1) },
    });
```

- `TParam` is the page parameter (an offset, a cursor, a page number); `TPage` is one page of data.
- `GetNextPageParam` returns the parameter for the next page, or `NextPageParam.None<T>()` when there are
  no more.
- `QueryOptions` controls caching and background behavior exactly as for regular queries (stale time,
  cache time, refetch triggers and interval, `Enabled`, retries).

## Pages live in the cache

The accumulated pages are stored under the query's key as an `InfiniteData<TParam, TPage>`. Infinite
queries are therefore deduplicated, invalidated, garbage collected, shown in DevTools, persisted and
hydrated like any other query - and every infinite query created for the same key shares the same pages.
Read or write them directly when needed:

```csharp
var pages = client.GetQueryData<InfiniteData<int, Page>>(QueryKey.Create("feed")).Data;
```

The instance observes the cache entry until it is **disposed**; after that the pages are garbage collected
once their cache time elapses.

## Loading pages

```csharp
await feed.FetchAsync();              // first page, or serve fresh cached pages (refetch them if stale)

while (feed.HasNextPage)
{
    await feed.FetchNextPageAsync();  // append the next page
}
```

`feed.CurrentState` exposes `Data.Pages`, `Data.PageParams`, `HasNextPage`, `HasPreviousPage`,
`IsFetchingNextPage`, `IsFetchingPreviousPage`, `IsPending`, `IsFetching`, `Error`, `FailureCount` and
`DataUpdatedAt`. Subscribe to `feed.StateChanged` to drive a UI (or use `UseInfiniteQuery` in Blazor).

`FetchNextPageAsync` while a background refetch is running cancels that refetch in favor of the page the
user asked for. Page cursors are always computed from the latest cached pages.

## Bidirectional pagination

Provide `GetPreviousPageParam` to page backward (e.g. a chat scrolled to the middle):

```csharp
new InfiniteQueryOptions<int, Page>
{
    InitialPageParam = 100,
    GetNextPageParam     = (last, _, _)  => NextPageParam.Of(last.Offset + 10),
    GetPreviousPageParam = (first, _, _) => first.Offset > 0 ? NextPageParam.Of(first.Offset - 10) : NextPageParam.None<int>(),
}
```

`FetchPreviousPageAsync()` prepends pages.

## Sliding window

Set `MaxPages` to cap memory - fetching in one direction drops pages from the opposite end.

```csharp
new InfiniteQueryOptions<int, Page> { /* ... */ MaxPages = 3 }
```

## Refetch and invalidation

`RefetchAsync()` reloads the loaded pages in order, starting from the first page's parameter and
**re-deriving** later parameters from the fresh pages - so cursor-based APIs stay correct even when items
were inserted or removed, and pages that no longer exist are dropped. Invalidating the key (or a prefix of
it) does the same for active infinite queries.
