using Bunit;
using DotNetQuery;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DotNetQuery.Blazor.Tests;

public class QueryComponentTests : TestContext
{
    public QueryComponentTests()
    {
        Services.AddDotNetQuery(o =>
        {
            o.DefaultRetryCount = 0;
            o.DefaultRetryDelay = RetryPolicy.Constant(TimeSpan.Zero);
            o.DefaultStaleTime = TimeSpan.FromMinutes(5);
        });
    }

    [Fact]
    public void Renders_loading_then_data()
    {
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var cut = RenderComponent<Query<int>>(ps => ps
            .Add(p => p.Key, QueryKey.Create("count"))
            .Add(p => p.Fetcher, (CancellationToken _) => gate.Task)
            .Add(p => p.ChildContent, result => result.IsLoading ? "<span>loading</span>" : $"<span>{result.Data}</span>"));

        // The Loading state is set once the fetch cycle begins (after its initial yield).
        cut.WaitForAssertion(() => Assert.Contains("loading", cut.Markup));

        gate.SetResult(42);

        cut.WaitForAssertion(() => Assert.Contains("42", cut.Markup));
    }

    [Fact]
    public void Renders_error_state()
    {
        var cut = RenderComponent<Query<int>>(ps => ps
            .Add(p => p.Key, QueryKey.Create("err"))
            .Add(p => p.Fetcher, (CancellationToken _) => Task.FromException<int>(new InvalidOperationException("boom")))
            .Add(p => p.ChildContent, result => result.IsError ? $"<span>error: {result.Error!.Message}</span>" : "<span>ok</span>"));

        cut.WaitForAssertion(() => Assert.Contains("error: boom", cut.Markup));
    }

    [Fact]
    public void Serves_cached_data_immediately_for_a_prewarmed_query()
    {
        var client = Services.GetRequiredService<IQueryClient>();
        client.SetQueryData(QueryKey.Create("warm"), 7);

        var fetcherCalls = 0;
        var cut = RenderComponent<Query<int>>(ps => ps
            .Add(p => p.Key, QueryKey.Create("warm"))
            .Add(p => p.Fetcher, (CancellationToken _) => { fetcherCalls++; return Task.FromResult(999); })
            .Add(p => p.ChildContent, result => $"<span>{result.Data}</span>"));

        cut.WaitForAssertion(() => Assert.Contains("7", cut.Markup));
        Assert.Equal(0, fetcherCalls); // fresh cache → fetcher not invoked
    }

    [Fact]
    public void Refetches_when_key_changes()
    {
        var cut = RenderComponent<Query<string>>(ps => ps
            .Add(p => p.Key, QueryKey.Create("item", 1))
            .Add(p => p.Fetcher, (CancellationToken _) => Task.FromResult("one"))
            .Add(p => p.ChildContent, result => $"<span>{result.Data}</span>"));

        cut.WaitForAssertion(() => Assert.Contains("one", cut.Markup));

        cut.SetParametersAndRender(ps => ps
            .Add(p => p.Key, QueryKey.Create("item", 2))
            .Add(p => p.Fetcher, (CancellationToken _) => Task.FromResult("two"))
            .Add(p => p.ChildContent, result => $"<span>{result.Data}</span>"));

        cut.WaitForAssertion(() => Assert.Contains("two", cut.Markup));
    }

    [Fact]
    public void Disposing_component_unsubscribes_observer()
    {
        var key = QueryKey.Create("dispose");
        var client = Services.GetRequiredService<IQueryClient>();

        var cut = RenderComponent<Query<int>>(ps => ps
            .Add(p => p.Key, key)
            .Add(p => p.Fetcher, (CancellationToken _) => Task.FromResult(1))
            .Add(p => p.ChildContent, result => $"<span>{result.Data}</span>"));

        cut.WaitForAssertion(() => Assert.Contains("1", cut.Markup));
        Assert.True(client.Cache.Describe().Single(d => d.Key == key).ObserverCount >= 1);

        DisposeComponents();

        Assert.Equal(0, client.Cache.Describe().Single(d => d.Key == key).ObserverCount);
    }
}
