using Bunit;
using Bunit.TestDoubles;
using DotNetQuery;
using DotNetQuery.Blazor;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DotNetQuery.Blazor.Tests;

public class ProviderAndNavigationTests : TestContext
{
    public ProviderAndNavigationTests()
    {
        Services.AddDotNetQueryBlazor(o =>
        {
            o.DefaultRetryCount = 0;
            o.DefaultStaleTime = TimeSpan.FromMinutes(5);
        });
    }

    [Fact]
    public void Components_use_the_client_supplied_by_a_provider()
    {
        using var isolated = new QueryClient(new QueryClientOptions { DefaultStaleTime = TimeSpan.FromMinutes(5) });
        isolated.SetQueryData(QueryKey.Create("who"), "provided client");
        Services.GetRequiredService<IQueryClient>().SetQueryData(QueryKey.Create("who"), "registered client");

        var cut = RenderComponent<QueryClientProvider>(ps => ps
            .Add(p => p.Client, isolated)
            .AddChildContent<Query<string>>(q => q
                .Add(p => p.Key, QueryKey.Create("who"))
                .Add(p => p.Fetcher, (CancellationToken _) => Task.FromResult("fetched"))
                .Add(p => p.ChildContent, r => $"<span>{r.Data}</span>")));

        Assert.Contains("provided client", cut.Markup);
    }

    [Fact]
    public async Task NotifyOn_data_skips_re_renders_for_status_flips()
    {
        var client = Services.GetRequiredService<IQueryClient>();
        var key = QueryKey.Create("quiet");
        var renders = 0;
        var cut = RenderComponent<Query<List<int>>>(ps => ps
            .Add(p => p.Key, key)
            .Add(p => p.Fetcher, (CancellationToken _) => Task.FromResult(new List<int> { 1, 2 }))
            .Add(p => p.NotifyOn, QueryStateProperties.Data | QueryStateProperties.Error)
            .Add(p => p.ChildContent, r => { Interlocked.Increment(ref renders); return $"<span>{r.Data?.Count}</span>"; }));
        cut.WaitForAssertion(() => Assert.Contains("2", cut.Markup));
        var before = Volatile.Read(ref renders);

        await client.RefetchQueryAsync(key); // Refetching -> Success with equal data
        await Task.Delay(100);

        Assert.Equal(before, Volatile.Read(ref renders));
    }

    private sealed class UsersView : DotNetQueryComponentBase
    {
        [Parameter]
        public IReadOnlyList<int> Ids { get; set; } = [];

        private IReadOnlyList<QueryObserver<string>> _users = [];

        protected override void OnParametersSet() =>
            _users = UseQueries(Ids.Select(id => new QueryDefinition<string>(QueryKey.Create("user", id), _ => Task.FromResult($"user {id}"))).ToList());

        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            builder.AddContent(0, string.Join(",", _users.Select(u => u.CurrentState.Data)));
    }

    [Fact]
    public void UseQueries_tracks_a_dynamic_list_of_queries()
    {
        var client = Services.GetRequiredService<IQueryClient>();
        var cut = RenderComponent<UsersView>(ps => ps.Add(p => p.Ids, [1, 2]));
        cut.WaitForAssertion(() => Assert.Contains("user 1,user 2", cut.Markup));

        cut.SetParametersAndRender(ps => ps.Add(p => p.Ids, [2, 3]));

        cut.WaitForAssertion(() => Assert.Contains("user 2,user 3", cut.Markup));
        Assert.Equal(0, client.Cache.Describe(QueryKey.Create("user", 1))!.ObserverCount);
        Assert.Equal(1, client.Cache.Describe(QueryKey.Create("user", 3))!.ObserverCount);
    }

    [Fact]
    public async Task Navigation_cancels_fetches_of_queries_left_behind()
    {
        var client = Services.GetRequiredService<IQueryClient>();
        var navigation = Services.GetRequiredService<FakeNavigationManager>();
        RenderComponent<DotNetQueryNavigation>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<int>();
        var leftBehind = client.FetchAsync(QueryKey.Create("old-page"), async ct => { started.TrySetResult(); return await never.Task.WaitAsync(ct); });
        await started.Task;

        navigation.NavigateTo("/next");

        var result = await leftBehind.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.HasData);
        Assert.Equal(0, client.CountFetching());
    }
}
