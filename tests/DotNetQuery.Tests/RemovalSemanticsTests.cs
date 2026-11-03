using DotNetQuery;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Tests;

public class RemovalSemanticsTests
{
    private static QueryClient CreateClient() =>
        new(TestUtil.NoRetry(o => o.DefaultStaleTime = TimeSpan.FromMinutes(5)), new FakeTimeProvider());

    [Fact]
    public async Task Removing_an_observed_query_resets_and_refetches_it_for_its_observers()
    {
        using var client = CreateClient();
        var key = QueryKey.Create("me");
        var user = "alice";
        using var observer = client.Watch<string>(key);
        await client.FetchAsync(key, _ => Task.FromResult(user));
        Assert.Equal("alice", observer.CurrentState.Data);

        user = "bob";
        Assert.True(client.RemoveQuery(key));

        // The stale data is gone immediately, and the observer receives the refetched data.
        Assert.NotEqual("alice", observer.CurrentState.Data);
        await TestUtil.WaitUntilAsync(() => observer.CurrentState.Data == "bob");
        Assert.Equal(1, client.Cache.Count);
    }

    [Fact]
    public async Task Clear_drops_unobserved_queries_and_reloads_observed_ones()
    {
        using var client = CreateClient();
        var observed = QueryKey.Create("observed");
        var background = QueryKey.Create("background");
        var version = 1;
        using var observer = client.Watch<int>(observed);
        await client.FetchAsync(observed, _ => Task.FromResult(version));
        await client.FetchAsync(background, _ => Task.FromResult(version));

        version = 2;
        client.Clear();

        Assert.Equal([observed], client.Cache.Keys);
        await TestUtil.WaitUntilAsync(() => observer.CurrentState.Data == 2);
    }

    [Fact]
    public async Task Disposing_the_client_disposes_observed_entries_too()
    {
        var client = CreateClient();
        var key = QueryKey.Create("disposed");
        var observer = client.Watch<int>(key);
        await client.FetchAsync(key, _ => Task.FromResult(1));
        var calls = 0;
        client.Cache.Changed += c => { if (c.Kind is QueryCacheChangeKind.Removed) { calls++; } };

        client.Dispose();

        Assert.Equal(0, client.Cache.Count);
        Assert.Equal(1, calls);
        observer.Dispose();
    }

    [Fact]
    public async Task Reset_mid_fetch_never_lets_the_outdated_result_land_even_if_the_fetcher_ignores_cancellation()
    {
        using var client = CreateClient();
        var key = QueryKey.Create("stubborn");
        var started = TestUtil.NewSignal();
        var release = TestUtil.NewSignal();
        var inFlight = client.FetchAsync(key, async _ =>
        {
            started.TrySetResult();
            await release.Task; // ignores the token
            return "outdated";
        });
        await started.Task;

        await client.ResetQueriesAsync(QueryFilter.ForKey(key));
        release.SetResult();
        var result = await inFlight;

        Assert.False(result.HasData);
        Assert.Equal(QueryStatus.Idle, client.GetQueryData<string>(key).Status);
        Assert.False(client.GetQueryData<string>(key).HasData);
    }

    [Fact]
    public async Task A_new_fetch_after_a_reset_is_not_affected_by_the_detached_one()
    {
        using var client = CreateClient();
        var key = QueryKey.Create("after-reset");
        var started = TestUtil.NewSignal();
        var release = TestUtil.NewSignal();
        _ = client.FetchAsync(key, async _ =>
        {
            started.TrySetResult();
            await release.Task;
            return "old";
        });
        await started.Task;
        await client.ResetQueriesAsync(QueryFilter.ForKey(key));

        var fresh = await client.FetchAsync(key, _ => Task.FromResult("new"));
        release.SetResult();
        await Task.Delay(20);

        Assert.Equal("new", fresh.Data);
        Assert.Equal("new", client.GetQueryData<string>(key).Data);
        Assert.Equal(QueryStatus.Success, client.GetQueryData<string>(key).Status);
    }
}
