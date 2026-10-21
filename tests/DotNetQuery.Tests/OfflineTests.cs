using System.Diagnostics;
using DotNetQuery;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Tests;

public class OfflineTests
{
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 8000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException("Condition was not met within the timeout.");
            }

            await Task.Delay(10);
        }
    }

    private static QueryClient CreateClient(FakeTimeProvider time, ConnectivityProvider connectivity) => new(
        new QueryClientOptions
        {
            DefaultRetryDelay = RetryPolicy.Constant(TimeSpan.Zero),
            DefaultRetryCount = 0,
            DefaultStaleTime = TimeSpan.FromMinutes(10),
            GarbageCollectionInterval = TimeSpan.FromHours(1),
        },
        time,
        connectivity: connectivity);

    [Fact]
    public async Task Offline_fetch_with_no_data_pauses_without_calling_fetcher()
    {
        var time = new FakeTimeProvider();
        var connectivity = new ConnectivityProvider();
        connectivity.SetOnline(false);
        using var client = CreateClient(time, connectivity);
        var calls = 0;

        var result = await client.FetchAsync(
            QueryKey.Create("off"),
            _ => { calls++; return Task.FromResult(1); });

        Assert.Equal(0, calls);
        Assert.Equal(QueryStatus.Paused, result.Status);
    }

    [Fact]
    public async Task Offline_fetch_serves_cached_data()
    {
        var time = new FakeTimeProvider();
        var connectivity = new ConnectivityProvider();
        using var client = CreateClient(time, connectivity);
        var key = QueryKey.Create("cached");

        client.SetQueryData(key, 42); // seeded while online
        connectivity.SetOnline(false);

        var result = await client.FetchAsync(key, _ => Task.FromResult(999));

        Assert.Equal(42, result.Data); // cached value, fetcher not used
    }

    [Fact]
    public async Task Reconnect_refetches_paused_active_query()
    {
        var time = new FakeTimeProvider();
        var connectivity = new ConnectivityProvider();
        using var client = CreateClient(time, connectivity);
        var key = QueryKey.Create("resume");
        var calls = 0;

        Task<int> Fetcher(CancellationToken _) => Task.FromResult(Interlocked.Increment(ref calls));

        using var observer = client.Watch<int>(key);
        connectivity.SetOnline(false);
        var paused = await client.FetchAsync(key, Fetcher);
        Assert.Equal(QueryStatus.Paused, paused.Status);
        Assert.Equal(0, calls);

        connectivity.SetOnline(true); // back online → auto refetch
        await WaitUntilAsync(() => calls == 1);
        await WaitUntilAsync(() => client.GetQueryData<int>(key).Status == QueryStatus.Success);
    }

    [Fact]
    public async Task Mutation_queue_defers_while_offline_and_replays_on_reconnect()
    {
        var connectivity = new ConnectivityProvider();
        using var queue = new OfflineMutationQueue(connectivity);
        var applied = new List<int>();

        connectivity.SetOnline(false);
        await queue.EnqueueAsync(_ => { applied.Add(1); return Task.CompletedTask; });
        await queue.EnqueueAsync(_ => { applied.Add(2); return Task.CompletedTask; });

        Assert.Equal(2, queue.PendingCount);
        Assert.Empty(applied);

        connectivity.SetOnline(true);
        await WaitUntilAsync(() => applied.Count == 2);

        Assert.Equal(new[] { 1, 2 }, applied); // replayed in order
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task Mutation_queue_runs_immediately_when_online()
    {
        var connectivity = new ConnectivityProvider(); // online by default
        using var queue = new OfflineMutationQueue(connectivity);
        var ran = false;

        await queue.EnqueueAsync(_ => { ran = true; return Task.CompletedTask; });

        Assert.True(ran);
        Assert.Equal(0, queue.PendingCount);
    }
}
