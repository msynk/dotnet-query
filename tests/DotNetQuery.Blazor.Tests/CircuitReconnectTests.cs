using DotNetQuery;
using DotNetQuery.Blazor.Server;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DotNetQuery.Blazor.Tests;

public class CircuitReconnectTests
{
    [Fact]
    public async Task A_reconnection_after_a_drop_refetches_the_stale_active_queries()
    {
        using var client = new QueryClient(new QueryClientOptions { DefaultRetryCount = 0 });
        var fetches = 0;
        using var shown = client.Watch<int>(QueryKey.Create("shown"));
        await client.FetchAsync(QueryKey.Create("shown"), _ => Task.FromResult(Interlocked.Increment(ref fetches)));
        await client.FetchAsync(QueryKey.Create("hidden"), _ => Task.FromResult(Interlocked.Increment(ref fetches)));
        var handler = new QueryCircuitHandler(client);

        await handler.OnConnectionUpAsync(null!, CancellationToken.None);   // first connection: nothing to do
        Assert.Equal(2, fetches);

        await handler.OnConnectionDownAsync(null!, CancellationToken.None);
        await handler.OnConnectionUpAsync(null!, CancellationToken.None);
        await WaitUntilAsync(() => Volatile.Read(ref fetches) == 3);
        await Task.Delay(50);
        Assert.Equal(3, fetches);                                          // only the rendered one
    }

    [Fact]
    public async Task RefetchOnCircuitReconnect_registers_one_scoped_handler()
    {
        var services = new ServiceCollection();
        services.AddDotNetQueryBlazor().RefetchOnCircuitReconnect().RefetchOnCircuitReconnect();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var handler = Assert.Single(scope.ServiceProvider.GetServices<CircuitHandler>());
        Assert.IsType<QueryCircuitHandler>(handler);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition was not met in time.");
            await Task.Delay(5);
        }
    }
}
