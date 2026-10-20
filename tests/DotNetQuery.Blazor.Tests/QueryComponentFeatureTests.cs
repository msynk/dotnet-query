using Bunit;
using Bunit.TestDoubles;
using DotNetQuery;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DotNetQuery.Blazor.Tests;

public class QueryComponentFeatureTests : TestContext
{
    public QueryComponentFeatureTests()
    {
        Services.AddDotNetQueryBlazor(o =>
        {
            o.DefaultRetryCount = 0;
            o.DefaultRetryDelay = RetryPolicy.Constant(TimeSpan.Zero);
            o.DefaultStaleTime = TimeSpan.FromMinutes(5);
        });
    }

    private IQueryClient Client => Services.GetRequiredService<IQueryClient>();

    [Fact]
    public void Renders_a_query_definition_with_loading_and_data_templates()
    {
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var definition = new QueryDefinition<string>(QueryKey.Create("greeting"), _ => gate.Task);

        var cut = RenderComponent<Query<string>>(ps => ps
            .Add(p => p.Definition, definition)
            .Add(p => p.Loading, "<span>loading…</span>")
            .Add(p => p.ChildContent, r => $"<span>data:{r.Data}</span>"));

        // The first render already shows the loading template (no idle flash).
        Assert.Contains("loading…", cut.Markup);

        gate.SetResult("hello");
        cut.WaitForAssertion(() => Assert.Contains("data:hello", cut.Markup));
    }

    [Fact]
    public void Error_template_renders_when_there_is_no_data()
    {
        var cut = RenderComponent<Query<int>>(ps => ps
            .Add(p => p.Key, QueryKey.Create("boom"))
            .Add(p => p.Fetcher, (CancellationToken _) => Task.FromException<int>(new InvalidOperationException("kaput")))
            .Add(p => p.Error, (RenderFragment<Exception>)(ex => b => b.AddContent(0, $"failed:{ex.Message}")))
            .Add(p => p.ChildContent, r => $"<span>{r.Data}</span>"));

        cut.WaitForAssertion(() => Assert.Contains("failed:kaput", cut.Markup));
    }

    [Fact]
    public void KeepPreviousData_renders_the_previous_page_while_the_next_page_loads()
    {
        var page2 = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var cut = RenderComponent<Query<string>>(ps => ps
            .Add(p => p.Key, QueryKey.Create("page", 1))
            .Add(p => p.Fetcher, (CancellationToken _) => Task.FromResult("page one"))
            .Add(p => p.KeepPreviousData, true)
            .Add(p => p.ChildContent, r => $"<span>{r.Data}|placeholder:{r.IsPlaceholderData}</span>"));
        cut.WaitForAssertion(() => Assert.Contains("page one|placeholder:False", cut.Markup));

        cut.SetParametersAndRender(ps => ps
            .Add(p => p.Key, QueryKey.Create("page", 2))
            .Add(p => p.Fetcher, (CancellationToken _) => page2.Task));
        cut.WaitForAssertion(() => Assert.Contains("page one|placeholder:True", cut.Markup));

        page2.SetResult("page two");
        cut.WaitForAssertion(() => Assert.Contains("page two|placeholder:False", cut.Markup));
    }

    [Fact]
    public void A_dependent_query_fetches_when_it_becomes_enabled()
    {
        var calls = 0;
        var key = QueryKey.Create("dependent");
        Task<int> Fetcher(CancellationToken _) => Task.FromResult(Interlocked.Increment(ref calls));

        var cut = RenderComponent<Query<int>>(ps => ps
            .Add(p => p.Key, key)
            .Add(p => p.Fetcher, Fetcher)
            .Add(p => p.Options, new QueryOptions { Enabled = false })
            .Add(p => p.ChildContent, r => $"<span>{r.Status}:{r.Data}</span>"));
        Assert.Equal(0, calls);

        cut.SetParametersAndRender(ps => ps.Add(p => p.Options, new QueryOptions { Enabled = true }));

        cut.WaitForAssertion(() => Assert.Contains("Success:1", cut.Markup));
    }

    [Fact]
    public void Hydrates_from_prerendered_state_without_refetching()
    {
        var appState = this.AddFakePersistentComponentState();
        var key = QueryKey.Create("todos");
        appState.Persist(QueryStateHydration.StateKey(key), new { Data = new[] { "from server" }, UpdatedAt = DateTimeOffset.UtcNow });
        var calls = 0;

        var cut = RenderComponent<Query<string[]>>(ps => ps
            .Add(p => p.Key, key)
            .Add(p => p.Fetcher, (CancellationToken _) => { calls++; return Task.FromResult(new[] { "from client" }); })
            .Add(p => p.ChildContent, r => $"<span>{string.Join(",", r.Data ?? [])}</span>"));

        cut.WaitForAssertion(() => Assert.Contains("from server", cut.Markup));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Persists_fetched_data_for_the_interactive_render()
    {
        var appState = this.AddFakePersistentComponentState();
        var key = QueryKey.Create("persist-me");

        var cut = RenderComponent<Query<int>>(ps => ps
            .Add(p => p.Key, key)
            .Add(p => p.Fetcher, (CancellationToken _) => Task.FromResult(42))
            .Add(p => p.ChildContent, r => $"<span>{r.Data}</span>"));
        cut.WaitForAssertion(() => Assert.Contains("42", cut.Markup));

        appState.TriggerOnPersisting();

        Assert.True(appState.TryTake<HydrationProbe>(QueryStateHydration.StateKey(key), out var persisted));
        Assert.Equal(42, persisted!.Data);
    }

    [Fact]
    public void AddDotNetQueryBlazor_gives_each_scope_its_own_cache()
    {
        var provider = new ServiceCollection().AddDotNetQueryBlazor().Services.BuildServiceProvider();
        using var circuitA = provider.CreateScope();
        using var circuitB = provider.CreateScope();

        var a = circuitA.ServiceProvider.GetRequiredService<IQueryClient>();
        var b = circuitB.ServiceProvider.GetRequiredService<IQueryClient>();
        a.SetQueryData(QueryKey.Create("user", "profile"), "alice");

        Assert.NotSame(a, b);
        Assert.False(b.GetQueryData<string>(QueryKey.Create("user", "profile")).HasData);
        Assert.Same(a, circuitA.ServiceProvider.GetRequiredService<IQueryClient>());
    }

    [Fact]
    public void Initial_online_report_does_not_trigger_a_refetch_storm()
    {
        var online = Services.GetRequiredService<OnlineManager>();
        var connectivity = Services.GetRequiredService<ConnectivityProvider>();
        var signals = Services.GetRequiredService<BrowserSignalService>();
        var raised = 0;
        online.Raised += () => raised++;

        signals.OnConnectivityChanged(true);   // initial report from the browser
        Assert.Equal(0, raised);

        signals.OnConnectivityChanged(false);
        Assert.False(connectivity.IsOnline);
        signals.OnConnectivityChanged(true);   // a real reconnect
        Assert.Equal(1, raised);
    }

    public sealed class HydrationProbe
    {
        public int Data { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }
    }
}
