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
    public void Scroll_restoration_tracks_and_restores_through_its_module()
    {
        var module = JSInterop.SetupModule("./_content/DotNetQuery.Blazor/dotnetquery-scroll.js");
        module.Mode = JSRuntimeMode.Loose;

        RenderComponent<ScrollRestoration>(ps => ps.Add(p => p.Id, "todos"));

        Assert.Contains(module.Invocations, i => i.Identifier == "track" && (string)i.Arguments[0]! == "todos");
        Assert.Contains(module.Invocations, i => i.Identifier == "restore");
    }
}
