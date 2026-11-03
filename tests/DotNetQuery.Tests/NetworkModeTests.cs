using DotNetQuery;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Tests;

public class NetworkModeTests
{
    private readonly ConnectivityProvider _connectivity = new();

    private QueryClient CreateClient(Action<QueryClientOptions>? configure = null) =>
        new(TestUtil.NoRetry(configure), new FakeTimeProvider(), connectivity: _connectivity);

    [Fact]
    public async Task Online_mode_pauses_offline_and_resumes_on_reconnect()
    {
        using var client = CreateClient();
        _connectivity.SetOnline(false);
        var calls = 0;
        var key = QueryKey.Create("online");

        var result = await client.FetchAsync(key, _ => Task.FromResult(++calls));

        Assert.True(result.IsPaused);
        Assert.Equal(0, calls);

        _connectivity.SetOnline(true);
        await TestUtil.WaitUntilAsync(() => client.GetQueryData<int>(key).IsSuccess);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Always_mode_fetches_while_offline()
    {
        using var client = CreateClient();
        _connectivity.SetOnline(false);

        var result = await client.FetchAsync(QueryKey.Create("always"), _ => Task.FromResult(7), new QueryOptions { NetworkMode = NetworkMode.Always });

        Assert.True(result.IsSuccess);
        Assert.Equal(7, result.Data);
    }

    [Fact]
    public async Task Always_mode_keeps_retrying_after_connectivity_drops()
    {
        using var client = CreateClient();
        var calls = 0;

        var result = await client.FetchAsync<int>(
            QueryKey.Create("always-retry"),
            _ =>
            {
                if (++calls == 1)
                {
                    _connectivity.SetOnline(false);
                    throw new InvalidOperationException("dropped");
                }

                return Task.FromResult(calls);
            },
            new QueryOptions { NetworkMode = NetworkMode.Always, RetryCount = 2 });

        Assert.True(result.IsSuccess);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Offline_first_runs_the_first_attempt_but_pauses_retries()
    {
        using var client = CreateClient(o => o.DefaultNetworkMode = NetworkMode.OfflineFirst);
        _connectivity.SetOnline(false);
        var calls = 0;
        var key = QueryKey.Create("offline-first");

        var result = await client.FetchAsync<int>(
            key,
            _ => ++calls == 1 ? throw new HttpRequestException("no network") : Task.FromResult(calls),
            new QueryOptions { RetryCount = 3 });

        Assert.Equal(1, calls);         // first attempt ran offline
        Assert.True(result.IsPaused);   // the retry waits for connectivity

        _connectivity.SetOnline(true);
        await TestUtil.WaitUntilAsync(() => client.GetQueryData<int>(key).IsSuccess);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Offline_first_serves_a_cache_hit_style_first_attempt_offline()
    {
        using var client = CreateClient();
        _connectivity.SetOnline(false);

        var result = await client.FetchAsync(QueryKey.Create("sw"), _ => Task.FromResult("from service worker"), new QueryOptions { NetworkMode = NetworkMode.OfflineFirst });

        Assert.Equal("from service worker", result.Data);
    }

    [Fact]
    public async Task Mutations_default_to_running_while_offline()
    {
        using var client = CreateClient();
        _connectivity.SetOnline(false);

        var result = await client.MutateAsync<int, int>((v, _) => Task.FromResult(v + 1), 1);

        Assert.Equal(2, result.Data);
    }

    [Fact]
    public async Task Online_mutations_wait_for_connectivity_while_applying_optimistic_updates()
    {
        using var client = CreateClient(o => o.DefaultMutationNetworkMode = NetworkMode.Online);
        var key = QueryKey.Create("todos");
        client.SetQueryData(key, 0);
        _connectivity.SetOnline(false);
        var calls = 0;
        var mutation = client.CreateMutation<int, int>(
            (v, _) => { calls++; return Task.FromResult(v); },
            new MutationOptions<int, int> { OptimisticUpdates = [OptimisticUpdate.Create<int, int>(key, (n, v) => n + v)] });

        var run = mutation.MutateAsync(5);

        await TestUtil.WaitUntilAsync(() => mutation.CurrentState.IsPaused);
        Assert.Equal(5, client.GetQueryData<int>(key).Data);
        Assert.Equal(0, calls);
        Assert.True(Assert.Single(client.Cache.Mutations.FindAll()).IsPaused);

        _connectivity.SetOnline(true);
        var result = await run;

        Assert.True(result.IsSuccess);
        Assert.Equal(1, calls);
        Assert.False(mutation.CurrentState.IsPaused);
    }

    [Fact]
    public async Task Cancelling_a_mutation_waiting_for_connectivity_rolls_back()
    {
        using var client = CreateClient();
        var key = QueryKey.Create("value");
        client.SetQueryData(key, "original");
        _connectivity.SetOnline(false);
        using var cts = new CancellationTokenSource();
        var mutation = client.CreateMutation<string, string>(
            (v, _) => Task.FromResult(v),
            new MutationOptions<string, string>
            {
                NetworkMode = NetworkMode.Online,
                OptimisticUpdates = [OptimisticUpdate.Create<string, string>(key, (_, v) => v)],
            });

        var run = mutation.MutateAsync("optimistic", cts.Token);
        await TestUtil.WaitUntilAsync(() => mutation.CurrentState.IsPaused);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal("original", client.GetQueryData<string>(key).Data);
    }

    [Fact]
    public async Task Offline_first_mutations_attempt_once_then_wait_before_retrying()
    {
        using var client = CreateClient();
        _connectivity.SetOnline(false);
        var calls = 0;
        var mutation = client.CreateMutation<int, int>(
            (v, _) => ++calls == 1 ? throw new HttpRequestException("offline") : Task.FromResult(v),
            new MutationOptions<int, int> { NetworkMode = NetworkMode.OfflineFirst, RetryCount = 2 });

        var run = mutation.MutateAsync(3);

        await TestUtil.WaitUntilAsync(() => mutation.CurrentState.IsPaused);
        Assert.Equal(1, calls);

        _connectivity.SetOnline(true);
        Assert.Equal(3, (await run).Data);
        Assert.Equal(2, calls);
    }
}
