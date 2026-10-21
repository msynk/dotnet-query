using DotNetQuery;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Tests;

public class QueryClientTests
{
    private static QueryClient CreateClient(FakeTimeProvider time, Action<QueryClientOptions>? configure = null)
    {
        var options = new QueryClientOptions
        {
            // Zero retry delay keeps retry tests deterministic without advancing the fake clock.
            DefaultRetryDelay = RetryPolicy.Constant(TimeSpan.Zero),
            DefaultRetryCount = 0,
        };
        configure?.Invoke(options);
        return new QueryClient(options, time);
    }

    [Fact]
    public async Task Fetch_returns_data_and_success_status()
    {
        var time = new FakeTimeProvider();
        using var client = CreateClient(time);

        var result = await client.FetchAsync(QueryKey.Create("n"), _ => Task.FromResult(123));

        Assert.True(result.IsSuccess);
        Assert.Equal(123, result.Data);
        Assert.Equal(QueryStatus.Success, result.Status);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Fresh_data_is_served_from_cache_without_refetching()
    {
        var time = new FakeTimeProvider();
        using var client = CreateClient(time, o => o.DefaultStaleTime = TimeSpan.FromMinutes(5));
        var key = QueryKey.Create("count");
        var calls = 0;

        Task<int> Fetcher(CancellationToken _) { calls++; return Task.FromResult(calls); }

        var first = await client.FetchAsync(key, Fetcher);
        var second = await client.FetchAsync(key, Fetcher);

        Assert.Equal(1, calls);
        Assert.Equal(1, first.Data);
        Assert.Equal(1, second.Data);
    }

    [Fact]
    public async Task Stale_data_is_refetched()
    {
        var time = new FakeTimeProvider();
        using var client = CreateClient(time, o => o.DefaultStaleTime = TimeSpan.FromSeconds(30));
        var key = QueryKey.Create("count");
        var calls = 0;

        Task<int> Fetcher(CancellationToken _) { calls++; return Task.FromResult(calls); }

        await client.FetchAsync(key, Fetcher);
        time.Advance(TimeSpan.FromMinutes(1)); // now stale
        var second = await client.FetchAsync(key, Fetcher);

        Assert.Equal(2, calls);
        Assert.Equal(2, second.Data);
    }

    [Fact]
    public async Task Concurrent_fetches_are_deduplicated()
    {
        var time = new FakeTimeProvider();
        using var client = CreateClient(time);
        var key = QueryKey.Create("dedup");
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        Task<int> Fetcher(CancellationToken _) { Interlocked.Increment(ref calls); return gate.Task; }

        var t1 = client.FetchAsync(key, Fetcher);
        var t2 = client.FetchAsync(key, Fetcher);
        gate.SetResult(7);
        var r1 = await t1;
        var r2 = await t2;

        Assert.Equal(1, calls);
        Assert.Equal(7, r1.Data);
        Assert.Equal(7, r2.Data);
    }

    [Fact]
    public async Task Failed_fetch_captures_error()
    {
        var time = new FakeTimeProvider();
        using var client = CreateClient(time);

        var result = await client.FetchAsync<int>(
            QueryKey.Create("boom"),
            _ => throw new InvalidOperationException("nope"));

        Assert.True(result.IsError);
        Assert.IsType<InvalidOperationException>(result.Error);
        Assert.Equal(QueryStatus.Error, result.Status);
    }

    [Fact]
    public async Task Retries_until_success()
    {
        var time = new FakeTimeProvider();
        using var client = CreateClient(time, o => o.DefaultRetryCount = 3);
        var attempts = 0;

        Task<string> Fetcher(CancellationToken _)
        {
            attempts++;
            if (attempts < 3)
            {
                throw new TimeoutException();
            }

            return Task.FromResult("ok");
        }

        var result = await client.FetchAsync(QueryKey.Create("retry"), Fetcher);

        Assert.Equal(3, attempts);
        Assert.True(result.IsSuccess);
        Assert.Equal("ok", result.Data);
    }

    [Fact]
    public async Task ThrowOnError_rethrows_after_retries_exhausted()
    {
        var time = new FakeTimeProvider();
        using var client = CreateClient(time);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.FetchAsync<int>(
                QueryKey.Create("throw"),
                _ => throw new InvalidOperationException(),
                new QueryOptions { ThrowOnError = true }));
    }

    [Fact]
    public async Task Disabled_query_does_not_fetch()
    {
        var time = new FakeTimeProvider();
        using var client = CreateClient(time);
        var calls = 0;

        var result = await client.FetchAsync(
            QueryKey.Create("disabled"),
            _ => { calls++; return Task.FromResult(1); },
            new QueryOptions { Enabled = false });

        Assert.Equal(0, calls);
        Assert.Equal(QueryStatus.Idle, result.Status);
    }

    [Fact]
    public async Task SetQueryData_seeds_the_cache()
    {
        var time = new FakeTimeProvider();
        using var client = CreateClient(time);
        var key = QueryKey.Create("seed");

        client.SetQueryData(key, 99);

        Assert.Equal(99, client.GetQueryData<int>(key).Data);
    }
}
