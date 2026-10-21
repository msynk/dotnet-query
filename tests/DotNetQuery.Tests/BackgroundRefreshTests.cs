using System.Diagnostics;
using DotNetQuery;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Tests;

public class BackgroundRefreshTests
{
    private static QueryClient CreateClient(FakeTimeProvider time) => new(
        new QueryClientOptions
        {
            DefaultRetryDelay = RetryPolicy.Constant(TimeSpan.Zero),
            DefaultRetryCount = 0,
            DefaultStaleTime = TimeSpan.FromMinutes(10),
            GarbageCollectionInterval = TimeSpan.FromHours(1),
        },
        time);

    // Background refetches are fire-and-forget: the interval timer fires synchronously on Advance
    // but the fetch itself completes on the thread pool, so we poll for the observed effect.
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

    [Fact]
    public async Task Active_query_refetches_on_the_configured_interval()
    {
        var time = new FakeTimeProvider();
        using var client = CreateClient(time);
        var key = QueryKey.Create("live");
        var calls = 0;

        Task<int> Fetcher(CancellationToken _) => Task.FromResult(Interlocked.Increment(ref calls));

        using var observer = client.Watch<int>(key); // active
        await client.FetchAsync(key, Fetcher, new QueryOptions { RefetchInterval = TimeSpan.FromSeconds(30) });
        Assert.Equal(1, calls);

        time.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => calls == 2);

        time.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => calls == 3);

        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Refetch_interval_stops_when_last_observer_is_disposed()
    {
        var time = new FakeTimeProvider();
        using var client = CreateClient(time);
        var key = QueryKey.Create("live2");
        var calls = 0;

        Task<int> Fetcher(CancellationToken _) => Task.FromResult(Interlocked.Increment(ref calls));

        var observer = client.Watch<int>(key);
        await client.FetchAsync(key, Fetcher, new QueryOptions { RefetchInterval = TimeSpan.FromSeconds(30) });
        Assert.Equal(1, calls);

        observer.Dispose(); // no more active observers → timer stops
        time.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(50); // give any erroneously-scheduled refetch a chance to run

        Assert.Equal(1, calls);
    }
}
