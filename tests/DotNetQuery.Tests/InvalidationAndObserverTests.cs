using DotNetQuery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Tests;

public class InvalidationAndObserverTests
{
    private static QueryClient CreateClient(FakeTimeProvider time, Action<QueryClientOptions>? configure = null)
    {
        var options = new QueryClientOptions
        {
            DefaultRetryDelay = RetryPolicy.Constant(TimeSpan.Zero),
            DefaultRetryCount = 0,
            DefaultStaleTime = TimeSpan.FromMinutes(5),
        };
        configure?.Invoke(options);
        return new QueryClient(options, time);
    }

    [Fact]
    public async Task Invalidate_refetches_active_query()
    {
        var time = new FakeTimeProvider();
        using var client = CreateClient(time);
        var key = QueryKey.Create("users", 1);
        var calls = 0;

        Task<int> Fetcher(CancellationToken _) { calls++; return Task.FromResult(calls); }

        using var observer = client.Watch<int>(key); // makes the query active
        await client.FetchAsync(key, Fetcher);
        Assert.Equal(1, calls);

        await client.InvalidateQueriesAsync(QueryKey.Create("users"));

        Assert.Equal(2, calls);
        Assert.Equal(2, client.GetQueryData<int>(key).Data);
    }

    [Fact]
    public async Task Observer_receives_state_changes()
    {
        var time = new FakeTimeProvider();
        using var client = CreateClient(time);
        var key = QueryKey.Create("watched");
        var received = new List<QueryStatus>();

        using var observer = client.Watch<int>(key);
        observer.StateChanged += s => received.Add(s.Status);

        await client.FetchAsync(key, _ => Task.FromResult(5));

        Assert.Contains(QueryStatus.Loading, received);
        Assert.Contains(QueryStatus.Success, received);
        Assert.Equal(5, observer.CurrentState.Data);
    }

    [Fact]
    public async Task Disposed_observer_stops_receiving_and_deactivates_entry()
    {
        var time = new FakeTimeProvider();
        using var client = CreateClient(time);
        var key = QueryKey.Create("x");

        var observer = client.Watch<int>(key);
        var count = 0;
        observer.StateChanged += _ => count++;
        observer.Dispose();

        await client.FetchAsync(key, _ => Task.FromResult(1));

        Assert.Equal(0, count);
    }

    [Fact]
    public void Garbage_collector_removes_expired_inactive_entries()
    {
        var time = new FakeTimeProvider();
        using var client = CreateClient(time, o =>
        {
            o.DefaultCacheTime = TimeSpan.FromSeconds(30);
            o.GarbageCollectionInterval = TimeSpan.FromHours(1); // isolate the manual collector
        });

        client.SetQueryData(QueryKey.Create("gc"), 1);
        Assert.Equal(1, client.Cache.Count);

        time.Advance(TimeSpan.FromMinutes(1));
        var collected = client.Cache.CollectGarbage();

        Assert.Equal(1, collected);
        Assert.Equal(0, client.Cache.Count);
    }

    [Fact]
    public void Active_entries_are_not_collected()
    {
        var time = new FakeTimeProvider();
        using var client = CreateClient(time, o => o.DefaultCacheTime = TimeSpan.FromSeconds(30));
        var key = QueryKey.Create("active");

        using var observer = client.Watch<int>(key);
        client.SetQueryData(key, 1);

        time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(0, client.Cache.CollectGarbage());
    }

    [Fact]
    public void DI_registration_resolves_singleton_client()
    {
        var services = new ServiceCollection();
        services.AddDotNetQuery(o => o.DefaultRetryCount = 5);
        using var provider = services.BuildServiceProvider();

        var client1 = provider.GetRequiredService<IQueryClient>();
        var client2 = provider.GetRequiredService<IQueryClient>();

        Assert.Same(client1, client2);
    }
}
