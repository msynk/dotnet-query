using DotNetQuery;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Core.Tests;

public class InfiniteQueryTests
{
    private static QueryClient CreateClient() => new(new QueryClientOptions(), new FakeTimeProvider());

    // Simulates an offset-paged API of 25 items, page size 10 → 3 pages.
    private sealed record Page(int Offset, IReadOnlyList<int> Items, bool HasMore);

    private static Task<Page> FetchPage(int offset, CancellationToken _)
    {
        const int total = 25;
        const int size = 10;
        var items = Enumerable.Range(offset, Math.Min(size, total - offset)).ToList();
        return Task.FromResult(new Page(offset, items, offset + size < total));
    }

    private InfiniteQuery<int, Page> CreateOffsetQuery(QueryClient client, int? maxPages = null) =>
        client.CreateInfiniteQuery<int, Page>(
            QueryKey.Create("feed"),
            FetchPage,
            new InfiniteQueryOptions<int, Page>
            {
                InitialPageParam = 0,
                MaxPages = maxPages,
                GetNextPageParam = (last, _, _) =>
                    last.HasMore ? NextPageParam.Of(last.Offset + 10) : NextPageParam.None<int>(),
                GetPreviousPageParam = (first, _, _) =>
                    first.Offset > 0 ? NextPageParam.Of(first.Offset - 10) : NextPageParam.None<int>(),
            });

    [Fact]
    public async Task Initial_fetch_loads_first_page()
    {
        using var client = CreateClient();
        using var query = CreateOffsetQuery(client);

        var state = await query.FetchAsync();

        Assert.True(state.IsSuccess);
        Assert.Single(state.Data.Pages);
        Assert.True(state.HasNextPage);
        Assert.False(state.HasPreviousPage);
        Assert.Equal(0, state.Data.Pages[0].Items[0]);
    }

    [Fact]
    public async Task Fetch_next_appends_pages_until_exhausted()
    {
        using var client = CreateClient();
        using var query = CreateOffsetQuery(client);

        await query.FetchAsync();
        await query.FetchNextPageAsync();
        var state = await query.FetchNextPageAsync();

        Assert.Equal(3, state.Data.Pages.Count);
        Assert.False(state.HasNextPage); // 25 items exhausted
        var allItems = state.Data.Pages.SelectMany(p => p.Items).ToList();
        Assert.Equal(Enumerable.Range(0, 25), allItems);
    }

    [Fact]
    public async Task Fetch_next_is_noop_when_no_next_page()
    {
        using var client = CreateClient();
        using var query = CreateOffsetQuery(client);

        await query.FetchAsync();
        await query.FetchNextPageAsync();
        await query.FetchNextPageAsync();
        var state = await query.FetchNextPageAsync(); // already exhausted

        Assert.Equal(3, state.Data.Pages.Count);
    }

    [Fact]
    public async Task Fetch_previous_prepends_pages()
    {
        using var client = CreateClient();
        using var query = client.CreateInfiniteQuery<int, Page>(
            QueryKey.Create("feed"),
            FetchPage,
            new InfiniteQueryOptions<int, Page>
            {
                InitialPageParam = 10, // start in the middle
                GetNextPageParam = (last, _, _) =>
                    last.HasMore ? NextPageParam.Of(last.Offset + 10) : NextPageParam.None<int>(),
                GetPreviousPageParam = (first, _, _) =>
                    first.Offset > 0 ? NextPageParam.Of(first.Offset - 10) : NextPageParam.None<int>(),
            });

        await query.FetchAsync();
        Assert.True(query.HasPreviousPage);

        var state = await query.FetchPreviousPageAsync();

        Assert.Equal(2, state.Data.Pages.Count);
        Assert.Equal(0, state.Data.Pages[0].Offset); // previous page prepended
        Assert.Equal(10, state.Data.Pages[1].Offset);
        Assert.False(state.HasPreviousPage);
    }

    [Fact]
    public async Task MaxPages_creates_a_sliding_window()
    {
        using var client = CreateClient();
        using var query = CreateOffsetQuery(client, maxPages: 2);

        await query.FetchAsync();
        await query.FetchNextPageAsync();
        var state = await query.FetchNextPageAsync();

        Assert.Equal(2, state.Data.Pages.Count); // capped
        Assert.Equal(10, state.Data.Pages[0].Offset); // first page dropped
        Assert.Equal(20, state.Data.Pages[1].Offset);
    }

    [Fact]
    public async Task Refetch_reloads_all_current_pages()
    {
        using var client = CreateClient();
        var fetches = 0;
        using var query = client.CreateInfiniteQuery<int, Page>(
            QueryKey.Create("feed"),
            (offset, ct) => { fetches++; return FetchPage(offset, ct); },
            new InfiniteQueryOptions<int, Page>
            {
                InitialPageParam = 0,
                GetNextPageParam = (last, _, _) =>
                    last.HasMore ? NextPageParam.Of(last.Offset + 10) : NextPageParam.None<int>(),
            });

        await query.FetchAsync();
        await query.FetchNextPageAsync();
        var beforeRefetch = fetches;

        var state = await query.RefetchAsync();

        Assert.Equal(beforeRefetch + 2, fetches); // both pages reloaded
        Assert.Equal(2, state.Data.Pages.Count);
    }

    [Fact]
    public async Task Invalidation_by_key_refetches_infinite_query()
    {
        using var client = CreateClient();
        var fetches = 0;
        using var query = client.CreateInfiniteQuery<int, Page>(
            QueryKey.Create("feed", "posts"),
            (offset, ct) => { fetches++; return FetchPage(offset, ct); },
            new InfiniteQueryOptions<int, Page>
            {
                InitialPageParam = 0,
                GetNextPageParam = (last, _, _) =>
                    last.HasMore ? NextPageParam.Of(last.Offset + 10) : NextPageParam.None<int>(),
            });

        await query.FetchAsync();
        await query.FetchNextPageAsync();
        var before = fetches; // 2 pages loaded

        await client.InvalidateQueriesAsync(QueryKey.Create("feed")); // prefix match

        Assert.Equal(before + 2, fetches); // both pages reloaded by invalidation
        Assert.Equal(2, query.CurrentState.Data.Pages.Count);
    }

    [Fact]
    public async Task Disposed_infinite_query_is_not_refetched_by_invalidation()
    {
        using var client = CreateClient();
        var fetches = 0;
        var query = client.CreateInfiniteQuery<int, Page>(
            QueryKey.Create("feed", "gone"),
            (offset, ct) => { fetches++; return FetchPage(offset, ct); },
            new InfiniteQueryOptions<int, Page>
            {
                InitialPageParam = 0,
                GetNextPageParam = (_, _, _) => NextPageParam.None<int>(),
            });

        await query.FetchAsync();
        query.Dispose(); // unregisters
        var before = fetches;

        await client.InvalidateQueriesAsync(QueryKey.Create("feed"));

        Assert.Equal(before, fetches);
    }

    [Fact]
    public async Task Failed_page_fetch_captures_error()
    {
        using var client = CreateClient();
        using var query = client.CreateInfiniteQuery<int, int>(
            QueryKey.Create("feed"),
            (_, _) => throw new InvalidOperationException("page failed"),
            new InfiniteQueryOptions<int, int>
            {
                InitialPageParam = 0,
                GetNextPageParam = (_, _, _) => NextPageParam.None<int>(),
            });

        var state = await query.FetchAsync();

        Assert.True(state.IsError);
        Assert.IsType<InvalidOperationException>(state.Error);
    }
}
