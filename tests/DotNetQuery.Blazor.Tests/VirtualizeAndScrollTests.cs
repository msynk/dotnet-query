using Bunit;
using DotNetQuery;
using DotNetQuery.Blazor;
using Microsoft.AspNetCore.Components.Web.Virtualization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DotNetQuery.Blazor.Tests;

public class VirtualizeAndScrollTests : TestContext
{
    private sealed record Page(int Offset, int[] Items, int Total);

    public VirtualizeAndScrollTests()
    {
        Services.AddDotNetQueryBlazor(o => o.DefaultRetryCount = 0);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public async Task Items_provider_loads_the_pages_covering_the_requested_rows()
    {
        var client = Services.GetRequiredService<IQueryClient>();
        var fetched = new List<int>();
        using var feed = client.CreateInfiniteQuery<int, Page>(
            QueryKey.Create("rows"),
            (offset, _) => { fetched.Add(offset); return Task.FromResult(new Page(offset, Enumerable.Range(offset, 20).ToArray(), 1000)); },
            new InfiniteQueryOptions<int, Page>
            {
                InitialPageParam = 0,
                GetNextPageParam = (last, _, _) => NextPageParam.Of(last.Offset + 20),
            });
        var provider = feed.ToItemsProvider(page => page.Items, page => page.Total);

        var result = await provider(new ItemsProviderRequest(30, 15, CancellationToken.None));

        Assert.Equal(Enumerable.Range(30, 15), result.Items);
        Assert.Equal(1000, result.TotalItemCount);
        Assert.Equal([0, 20, 40], fetched); // pages 0..2 cover rows 30..44

        await provider(new ItemsProviderRequest(0, 10, CancellationToken.None));
        Assert.Equal(3, fetched.Count); // cached pages are reused
    }

    [Fact]
    public async Task Items_provider_without_a_total_keeps_one_page_of_headroom()
    {
        var client = Services.GetRequiredService<IQueryClient>();
        using var feed = client.CreateInfiniteQuery<int, Page>(
            QueryKey.Create("unknown-total"),
            (offset, _) => Task.FromResult(new Page(offset, Enumerable.Range(offset, offset < 40 ? 20 : 5).ToArray(), -1)),
            new InfiniteQueryOptions<int, Page>
            {
                InitialPageParam = 0,
                GetNextPageParam = (last, _, _) => last.Items.Length == 20 ? NextPageParam.Of(last.Offset + 20) : NextPageParam.None<int>(),
            });
        var provider = feed.ToItemsProvider(page => page.Items);

        var first = await provider(new ItemsProviderRequest(0, 10, CancellationToken.None));
        Assert.Equal(40, first.TotalItemCount); // 20 loaded + 20 headroom

        var end = await provider(new ItemsProviderRequest(40, 20, CancellationToken.None));
        Assert.Equal(45, end.TotalItemCount);   // last page reached
        Assert.Equal(5, end.Items.Count());
    }

    [Fact]
    public async Task Items_provider_follows_the_sliding_window_of_max_pages()
    {
        var client = Services.GetRequiredService<IQueryClient>();
        var fetched = new List<int>();
        using var feed = client.CreateInfiniteQuery<int, Page>(
            QueryKey.Create("sliding"),
            (offset, _) => { fetched.Add(offset); return Task.FromResult(new Page(offset, Enumerable.Range(offset, 50).ToArray(), 1000)); },
            new InfiniteQueryOptions<int, Page>
            {
                InitialPageParam = 0,
                GetNextPageParam = (last, _, _) => last.Offset + 50 < 1000 ? NextPageParam.Of(last.Offset + 50) : NextPageParam.None<int>(),
                GetPreviousPageParam = (first, _, _) => first.Offset > 0 ? NextPageParam.Of(first.Offset - 50) : NextPageParam.None<int>(),
                MaxPages = 3,
            });
        var provider = feed.ToItemsProvider(page => page.Items, page => page.Total);

        var far = await provider(new ItemsProviderRequest(200, 20, CancellationToken.None));

        Assert.Equal(Enumerable.Range(200, 20), far.Items); // pages 100..249 are loaded; rows map to their real index
        Assert.Equal([0, 50, 100, 150, 200], fetched);
        Assert.Equal(3, feed.CurrentState.PageCount);

        var back = await provider(new ItemsProviderRequest(10, 20, CancellationToken.None));

        Assert.Equal(Enumerable.Range(10, 20), back.Items); // scrolled back before the window: previous pages reloaded
        Assert.Equal(1000, back.TotalItemCount);
    }

    [Fact]
    public async Task Items_provider_places_a_single_page_window()
    {
        var client = Services.GetRequiredService<IQueryClient>();
        using var feed = client.CreateInfiniteQuery<int, Page>(
            QueryKey.Create("one-page"),
            (offset, _) => Task.FromResult(new Page(offset, Enumerable.Range(offset, 10).ToArray(), -1)),
            new InfiniteQueryOptions<int, Page>
            {
                InitialPageParam = 0,
                GetNextPageParam = (last, _, _) => last.Offset < 90 ? NextPageParam.Of(last.Offset + 10) : NextPageParam.None<int>(),
                MaxPages = 1,
            });
        var provider = feed.ToItemsProvider(page => page.Items);

        var result = await provider(new ItemsProviderRequest(35, 5, CancellationToken.None));

        Assert.Equal(Enumerable.Range(35, 5), result.Items);
        Assert.Equal(50, result.TotalItemCount); // rows 30..39 loaded + one page of headroom
    }

    [Fact]
    public void Scroll_restoration_tracks_and_restores_through_its_module()
    {
        var module = JSInterop.SetupModule("./_content/DotNetQuery.Blazor/dotnetquery-scroll.js");
        module.Mode = JSRuntimeMode.Loose;

        RenderComponent<ScrollRestoration>(ps => ps.Add(p => p.Id, "todos"));

        Assert.Contains(module.Invocations, i => i.Identifier == "track" && (string)i.Arguments[0]! == "todos");
        Assert.Contains(module.Invocations, i => i.Identifier == "restore");
    }
}
