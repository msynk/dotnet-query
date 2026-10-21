using DotNetQuery;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static DotNetQuery.Tests.TestUtil;

namespace DotNetQuery.Tests;

/// <summary>Every refetch path honors connectivity, and paused work resumes on reconnect.</summary>
public class OfflineBehaviorTests
{
    private static QueryClient CreateClient(FakeTimeProvider time, ConnectivityProvider connectivity, Action<QueryClientOptions>? configure = null) =>
        new(NoRetry(configure), time, connectivity: connectivity);

    [Fact]
    public async Task Invalidation_while_offline_does_not_hit_the_network()
    {
        var connectivity = new ConnectivityProvider();
        using var client = CreateClient(new FakeTimeProvider(), connectivity);
        var key = QueryKey.Create("offline-invalidate");
        var calls = 0;
        Task<int> Fetcher(CancellationToken _) => Task.FromResult(Interlocked.Increment(ref calls));
        using var observer = client.Watch<int>(key);
        await client.FetchAsync(key, Fetcher);

        connectivity.SetOnline(false);
        await client.InvalidateQueriesAsync(key);

        Assert.Equal(1, calls);
        Assert.True(client.GetQueryData<int>(key).IsSuccess); // cached data is still served

        connectivity.SetOnline(true);
        await WaitUntilAsync(() => Volatile.Read(ref calls) == 2);
    }

    [Fact]
    public async Task Paused_fetch_resumes_on_reconnect_even_without_observers()
    {
        var connectivity = new ConnectivityProvider();
        connectivity.SetOnline(false);
        using var client = CreateClient(new FakeTimeProvider(), connectivity, o => o.DefaultRefetchOnReconnect = false);
        var key = QueryKey.Create("paused-imperative");

        var paused = await client.FetchAsync(key, _ => Task.FromResult(42));
        Assert.True(paused.IsPaused);

        connectivity.SetOnline(true);

        await WaitUntilAsync(() => client.GetQueryData<int>(key).Data == 42);
    }

    [Fact]
    public async Task Fresh_cached_data_while_offline_is_not_scheduled_for_refetch()
    {
        var time = new FakeTimeProvider();
        var connectivity = new ConnectivityProvider();
        using var client = CreateClient(time, connectivity, o =>
        {
            o.DefaultStaleTime = TimeSpan.FromMinutes(10);
            o.DefaultRefetchOnReconnect = false;
        });
        var key = QueryKey.Create("fresh-offline");
        var calls = 0;
        Task<int> Fetcher(CancellationToken _) => Task.FromResult(Interlocked.Increment(ref calls));
        await client.FetchAsync(key, Fetcher);

        connectivity.SetOnline(false);
        await client.FetchAsync(key, Fetcher); // fresh → served, nothing to pause
        connectivity.SetOnline(true);
        await Task.Delay(50);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Going_offline_mid_retry_pauses_instead_of_failing()
    {
        var time = new FakeTimeProvider();
        var connectivity = new ConnectivityProvider();
        using var client = CreateClient(time, connectivity, o => o.DefaultRetryCount = 5);
        var key = QueryKey.Create("offline-mid-retry");
        var calls = 0;

        Task<int> Fetcher(CancellationToken _)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                connectivity.SetOnline(false); // the network drops while the request is failing
                throw new HttpRequestException("network down");
            }

            return Task.FromResult(7);
        }

        var result = await client.FetchAsync(key, Fetcher);

        Assert.True(result.IsPaused);
        Assert.False(result.IsError);
        Assert.Equal(1, calls);

        connectivity.SetOnline(true);
        await WaitUntilAsync(() => client.GetQueryData<int>(key).Data == 7);
    }

    [Fact]
    public async Task Refetch_interval_does_not_hit_the_network_while_offline()
    {
        var time = new FakeTimeProvider();
        var connectivity = new ConnectivityProvider();
        using var client = CreateClient(time, connectivity);
        var key = QueryKey.Create("offline-interval");
        var calls = 0;
        Task<int> Fetcher(CancellationToken _) => Task.FromResult(Interlocked.Increment(ref calls));
        using var observer = client.Watch<int>(key);
        await client.FetchAsync(key, Fetcher, new QueryOptions { RefetchInterval = TimeSpan.FromSeconds(5) });

        connectivity.SetOnline(false);
        time.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(50);

        Assert.Equal(1, calls);
    }
}
