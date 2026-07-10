# Infinite Queries

An infinite query accumulates *pages* of data — for "load more" lists, feeds, and cursor-based APIs. It supports forward, backward, and bidirectional pagination.

## Creating one

```csharp
var feed = client.CreateInfiniteQuery<int, Page>(
    QueryKey.Create("feed"),
    (offset, ct) => api.GetPageAsync(offset, ct),
    new InfiniteQueryOptions<int, Page>
    {
        InitialPageParam = 0,
        GetNextPageParam = (lastPage, pages, pageParams) =>
            lastPage.HasMore ? NextPageParam.Of(lastPage.Offset + 10) : NextPageParam.None<int>(),
    });
```

- `TParam` is the page parameter (an offset, a cursor, a page number).
- `TPage` is one page of data.
- `GetNextPageParam` returns the parameter for the next page, or `NextPageParam.None<T>()` when there are no more.

## Loading pages

```csharp
await feed.FetchAsync();              // load the first page

while (feed.HasNextPage)
{
    await feed.FetchNextPageAsync();  // append the next page
}
```

The state (`feed.CurrentState`) exposes `Data.Pages`, `Data.PageParams`, `HasNextPage`, `HasPreviousPage`, `IsFetchingNextPage`, `IsFetchingPreviousPage`, and the usual status helpers. Subscribe via `feed.StateChanged` to drive a UI.

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

Set `MaxPages` to cap memory — fetching in one direction drops a page from the opposite end.

```csharp
new InfiniteQueryOptions<int, Page> { /* ... */ MaxPages = 3 }
```

## Refetch and invalidation

`RefetchAsync()` reloads every currently loaded page in order. Because the query is registered under its key, `client.InvalidateQueriesAsync(QueryKey.Create("feed"))` refetches it too. **Dispose the infinite query** when you're done so it unregisters.

## Ordering guarantee

Page operations are serialized internally, so concurrent `FetchNextPageAsync`/`FetchPreviousPageAsync` calls can never interleave and corrupt page order.
