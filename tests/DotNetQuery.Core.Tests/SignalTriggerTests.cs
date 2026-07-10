using System.Diagnostics;
using DotNetQuery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Core.Tests;

public class SignalTriggerTests
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

    private static (QueryClient Client, FocusManager Focus, OnlineManager Online) CreateClient(
        FakeTimeProvider time,
        TimeSpan staleTime)
    {
        var focus = new FocusManager();
        var online = new OnlineManager();
        var client = new QueryClient(
            new QueryClientOptions
            {
                DefaultRetryDelay = RetryPolicy.Constant(TimeSpan.Zero),
                DefaultRetryCount = 0,
                DefaultStaleTime = staleTime,
                GarbageCollectionInterval = TimeSpan.FromHours(1),
            },
            time,
            focus,
            online);
        return (client, focus, online);
    }

    [Fact]
    public async Task Focus_signal_refetches_active_stale_query()
    {
        var time = new FakeTimeProvider();
        var (client, focus, _) = CreateClient(time, TimeSpan.FromSeconds(30));
        using var _c = client;
        var key = QueryKey.Create("f");
        var calls = 0;

        Task<int> Fetcher(CancellationToken _) => Task.FromResult(Interlocked.Increment(ref calls));

        using var observer = client.Watch<int>(key);
        await client.FetchAsync(key, Fetcher);
        Assert.Equal(1, calls);

        time.Advance(TimeSpan.FromMinutes(1)); // becomes stale
        focus.Signal();

        await WaitUntilAsync(() => calls == 2);
    }

    [Fact]
    public async Task Focus_signal_does_not_refetch_fresh_query()
    {
        var time = new FakeTimeProvider();
        var (client, focus, _) = CreateClient(time, TimeSpan.FromMinutes(10));
        using var _c = client;
        var key = QueryKey.Create("fresh");
        var calls = 0;

        Task<int> Fetcher(CancellationToken _) => Task.FromResult(Interlocked.Increment(ref calls));

        using var observer = client.Watch<int>(key);
        await client.FetchAsync(key, Fetcher);

        focus.Signal(); // still fresh → no refetch
        await Task.Delay(50);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Focus_signal_does_not_refetch_inactive_query()
    {
        var time = new FakeTimeProvider();
        var (client, focus, _) = CreateClient(time, TimeSpan.FromSeconds(30));
        using var _c = client;
        var key = QueryKey.Create("inactive");
        var calls = 0;

        Task<int> Fetcher(CancellationToken _) => Task.FromResult(Interlocked.Increment(ref calls));

        await client.FetchAsync(key, Fetcher); // no observer → inactive
        time.Advance(TimeSpan.FromMinutes(1));

        focus.Signal();
        await Task.Delay(50);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Reconnect_signal_respects_per_query_opt_out()
    {
        var time = new FakeTimeProvider();
        var (client, _, online) = CreateClient(time, TimeSpan.FromSeconds(30));
        using var _c = client;
        var key = QueryKey.Create("noreconnect");
        var calls = 0;

        Task<int> Fetcher(CancellationToken _) => Task.FromResult(Interlocked.Increment(ref calls));

        using var observer = client.Watch<int>(key);
        await client.FetchAsync(key, Fetcher, new QueryOptions { RefetchOnReconnect = false });
        time.Advance(TimeSpan.FromMinutes(1));

        online.Signal();
        await Task.Delay(50);

        Assert.Equal(1, calls);
    }

    [Fact]
    public void DI_registers_signal_managers_as_singletons()
    {
        var services = new ServiceCollection();
        services.AddDotNetQuery();
        using var provider = services.BuildServiceProvider();

        var focusByInterface = provider.GetRequiredService<IFocusManager>();
        var focusByConcrete = provider.GetRequiredService<FocusManager>();

        Assert.Same(focusByConcrete, focusByInterface);
        Assert.NotNull(provider.GetRequiredService<IQueryClient>());
    }
}
