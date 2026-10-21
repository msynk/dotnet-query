using DotNetQuery;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Tests;

public class ResultAndPrefetchTests
{
    private static QueryClient CreateClient() =>
        new(new QueryClientOptions { DefaultRetryDelay = RetryPolicy.Constant(TimeSpan.Zero), DefaultRetryCount = 0 }, new FakeTimeProvider());

    [Fact]
    public void QueryResult_EnsureData_returns_data_on_success()
    {
        using var client = CreateClient();
        var key = QueryKey.Create("r");
        client.SetQueryData(key, 42);

        Assert.Equal(42, client.GetQueryData<int>(key).EnsureData());
    }

    [Fact]
    public void QueryResult_EnsureData_throws_when_no_data()
    {
        using var client = CreateClient();
        Assert.Throws<InvalidOperationException>(() => client.GetQueryData<int>(QueryKey.Create("missing")).EnsureData());
    }

    [Fact]
    public async Task QueryResult_EnsureData_throws_captured_error()
    {
        using var client = CreateClient();
        var key = QueryKey.Create("e");
        await client.FetchAsync<int>(key, _ => throw new InvalidOperationException("boom"));

        Assert.Throws<InvalidOperationException>(() => client.GetQueryData<int>(key).EnsureData());
    }

    [Fact]
    public async Task MutationResult_EnsureData_returns_and_throws()
    {
        using var client = CreateClient();

        var ok = await client.MutateAsync<int, int>((v, _) => Task.FromResult(v), 5);
        Assert.Equal(5, ok.EnsureData());

        var failed = await client.MutateAsync<int, int>(
            (_, _) => throw new InvalidOperationException("x"),
            0,
            new MutationOptions<int, int> { ThrowOnError = false });
        Assert.Throws<InvalidOperationException>(() => failed.EnsureData());
    }

    [Fact]
    public async Task Prefetch_populates_the_cache()
    {
        using var client = CreateClient();
        var key = QueryKey.Create("p");

        await client.PrefetchAsync(key, _ => Task.FromResult(7));

        Assert.Equal(7, client.GetQueryData<int>(key).Data);
    }

    [Fact]
    public async Task Prefetch_swallows_errors()
    {
        using var client = CreateClient();
        var key = QueryKey.Create("pe");

        await client.PrefetchAsync<int>(key, _ => throw new InvalidOperationException()); // must not throw

        Assert.Equal(QueryStatus.Error, client.GetQueryData<int>(key).Status);
    }

    [Fact]
    public async Task IsPending_is_true_until_data_or_error_arrives()
    {
        using var client = CreateClient();
        var key = QueryKey.Create("pending");

        // Idle (never fetched) counts as pending.
        Assert.True(client.GetQueryData<int>(key).IsPending);

        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetch = client.FetchAsync(key, _ => gate.Task);

        // Loading counts as pending; still pending mid-flight.
        Assert.True(client.GetQueryData<int>(key).IsPending);

        gate.SetResult(1);
        await fetch;

        Assert.False(client.GetQueryData<int>(key).IsPending); // has data now
    }

    [Fact]
    public void NextPageParam_helpers()
    {
        var some = NextPageParam.Of(5);
        Assert.True(some.HasValue);
        Assert.Equal(5, some.Value);

        Assert.False(NextPageParam.None<int>().HasValue);
        Assert.False(NextPageParam<int>.None.HasValue);
    }
}
