using DotNetQuery;
using Xunit;

namespace DotNetQuery.Tests;

public class InfinitePaginationTests
{
    private sealed record Page(int Offset, int[] Items, int Total);

    private static InfiniteQueryOptions<int, Page> Options => new()
    {
        InitialPageParam = 0,
        GetNextPageParam = (last, _, _) => last.Offset + last.Items.Length < last.Total ? NextPageParam.Of(last.Offset + last.Items.Length) : NextPageParam.None<int>(),
    };

    private static Task<Page> Fetch(int offset, CancellationToken _) =>
        Task.FromResult(new Page(offset, Enumerable.Range(offset, Math.Min(10, 35 - offset)).ToArray(), 35));

    [Fact]
    public async Task FetchPagesAsync_loads_until_the_count_or_the_last_page()
    {
        using var client = new QueryClient(TestUtil.NoRetry());
        using var feed = client.CreateInfiniteQuery<int, Page>(QueryKey.Create("feed"), Fetch, Options);

        var three = await feed.FetchPagesAsync(3);
        Assert.Equal(3, three.PageCount);

        var all = await feed.FetchPagesAsync(10);
        Assert.Equal(4, all.PageCount);
        Assert.False(all.HasNextPage);
        Assert.Equal(Enumerable.Range(0, 35), all.Data.Items(page => page.Items));
    }

    [Fact]
    public async Task Prefetching_an_infinite_query_warms_the_cache_for_the_next_instance()
    {
        using var client = new QueryClient(TestUtil.NoRetry(o => o.DefaultStaleTime = TimeSpan.FromMinutes(5)));
        var fetches = 0;
        Task<Page> Counting(int offset, CancellationToken ct) { fetches++; return Fetch(offset, ct); }

        await client.PrefetchInfiniteQueryAsync<int, Page>(QueryKey.Create("feed"), Counting, Options, pages: 2);
        using var feed = client.CreateInfiniteQuery<int, Page>(QueryKey.Create("feed"), Counting, Options);
        var state = await feed.FetchAsync();

        Assert.Equal(2, state.PageCount);
        Assert.Equal(2, fetches);
    }
}
