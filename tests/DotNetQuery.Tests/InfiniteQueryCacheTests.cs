using DotNetQuery;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static DotNetQuery.Tests.TestUtil;

namespace DotNetQuery.Tests;

/// <summary>Infinite queries are first-class cache entries.</summary>
public class InfiniteQueryCacheTests
{
    private sealed record Page(int Offset, IReadOnlyList<int> Items, bool HasMore);

    private static InfiniteQueryOptions<int, Page> Options(QueryOptions? queryOptions = null) => new()
    {
        InitialPageParam = 0,
        GetNextPageParam = (last, _, _) => last.HasMore ? NextPageParam.Of(last.Offset + 10) : NextPageParam.None<int>(),
        QueryOptions = queryOptions,
    };

    private static Func<int, CancellationToken, Task<Page>> Api(int total, Action<int>? onFetch = null) => (offset, _) =>
    {
        onFetch?.Invoke(offset);
        var items = Enumerable.Range(offset, Math.Max(0, Math.Min(10, total - offset))).ToList();
        return Task.FromResult(new Page(offset, items, offset + 10 < total));
    };

    [Fact]
    public async Task Pages_live_in_the_cache_and_are_visible_to_inspection()
    {
        using var client = new QueryClient(NoRetry(), new FakeTimeProvider());
        var key = QueryKey.Create("feed");
        using var feed = client.CreateInfiniteQuery(key, Api(25), Options());

        await feed.FetchAsync();
        await feed.FetchNextPageAsync();

        var cached = client.GetQueryData<InfiniteData<int, Page>>(key);
        Assert.Equal(2, cached.Data!.Pages.Count);
        var descriptor = client.Cache.Describe(key)!;
        Assert.Equal(1, descriptor.ObserverCount);
        Assert.Equal("InfiniteData<Int32,Page>", descriptor.DataTypeName);
    }

    [Fact]
    public async Task Two_instances_for_the_same_key_share_pages()
    {
        using var client = new QueryClient(NoRetry(o => o.DefaultStaleTime = TimeSpan.FromMinutes(5)), new FakeTimeProvider());
        var key = QueryKey.Create("shared-feed");
        var fetches = 0;
        using var a = client.CreateInfiniteQuery(key, Api(25, _ => Interlocked.Increment(ref fetches)), Options());
        using var b = client.CreateInfiniteQuery(key, Api(25, _ => Interlocked.Increment(ref fetches)), Options());

        await a.FetchAsync();
        await a.FetchNextPageAsync();
        await b.FetchAsync(); // fresh → served from cache

        Assert.Equal(2, b.CurrentState.Data.Pages.Count);
        Assert.Equal(2, fetches);
    }

    [Fact]
    public async Task Fresh_pages_are_not_refetched_and_stale_pages_refetch_in_order()
    {
        var time = new FakeTimeProvider();
        using var client = new QueryClient(NoRetry(), time);
        var offsets = new List<int>();
        using var feed = client.CreateInfiniteQuery(
            QueryKey.Create("stale-feed"),
            Api(25, o => { lock (offsets) { offsets.Add(o); } }),
            Options(new QueryOptions { StaleTime = TimeSpan.FromMinutes(1) }));

        await feed.FetchAsync();
        await feed.FetchNextPageAsync();
        await feed.FetchAsync();
        Assert.Equal([0, 10], offsets);

        time.Advance(TimeSpan.FromMinutes(2));
        await feed.FetchAsync();
        Assert.Equal([0, 10, 0, 10], offsets);
    }

    [Fact]
    public async Task Refetch_re_derives_cursors_and_drops_pages_that_no_longer_exist()
    {
        using var client = new QueryClient(NoRetry(), new FakeTimeProvider());
        var total = 25;
        using var feed = client.CreateInfiniteQuery(
            QueryKey.Create("shrinking"),
            (offset, ct) => Api(total)(offset, ct),
            Options());
        await feed.FetchAsync();
        await feed.FetchNextPageAsync();
        await feed.FetchNextPageAsync();
        Assert.Equal(3, feed.CurrentState.Data.Pages.Count);

        total = 15; // items were deleted on the server
        var state = await feed.RefetchAsync();

        Assert.Equal(2, state.Data.Pages.Count);
        Assert.False(state.HasNextPage);
    }

    [Fact]
    public async Task Next_page_reports_direction_while_in_flight()
    {
        using var client = new QueryClient(NoRetry(), new FakeTimeProvider());
        var gate = NewSignal();
        var flags = new List<bool>();
        using var feed = client.CreateInfiniteQuery<int, Page>(
            QueryKey.Create("direction"),
            async (offset, ct) =>
            {
                if (offset > 0)
                {
                    await gate.Task;
                }

                return await Api(25)(offset, ct);
            },
            Options());
        await feed.FetchAsync();
        feed.StateChanged += s => { lock (flags) { flags.Add(s.IsFetchingNextPage); } };

        var next = feed.FetchNextPageAsync();
        await WaitUntilAsync(() => feed.CurrentState.IsFetchingNextPage);
        gate.SetResult();
        var state = await next;

        Assert.False(state.IsFetchingNextPage);
        Assert.Equal(2, state.Data.Pages.Count);
        lock (flags)
        {
            Assert.Contains(true, flags);
        }
    }

    [Fact]
    public async Task Disposed_infinite_query_releases_its_observer_for_garbage_collection()
    {
        var time = new FakeTimeProvider();
        using var client = new QueryClient(NoRetry(o => o.DefaultCacheTime = TimeSpan.FromMinutes(1)), time);
        var key = QueryKey.Create("gc-feed");
        var feed = client.CreateInfiniteQuery(key, Api(25), Options());
        await feed.FetchAsync();

        feed.Dispose();
        time.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(1, client.Cache.CollectGarbage());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => feed.FetchNextPageAsync());
    }

    [Fact]
    public async Task Pages_can_be_updated_optimistically_through_the_cache()
    {
        using var client = new QueryClient(NoRetry(), new FakeTimeProvider());
        var key = QueryKey.Create("optimistic-feed");
        using var feed = client.CreateInfiniteQuery(key, Api(25), Options());
        await feed.FetchAsync();
        InfiniteQueryState<int, Page>? last = null;
        feed.StateChanged += s => last = s;

        client.SetQueryData<InfiniteData<int, Page>>(key, data => data! with
        {
            Pages = [data.Pages[0] with { Items = [-1, .. data.Pages[0].Items] }],
        });

        Assert.Equal(-1, feed.CurrentState.Data.Pages[0].Items[0]);
        Assert.NotNull(last);
    }
}
