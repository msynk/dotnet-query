using DotNetQuery;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Core.Tests;

public class CancellationPolicyTests
{
    [Fact]
    public async Task Cancels_in_flight_fetch_when_last_observer_leaves_and_opted_in()
    {
        var options = new QueryClientOptions
        {
            DefaultCancelInFlightWhenUnobserved = true,
            DefaultRetryCount = 0,
        };
        using var client = new QueryClient(options, new FakeTimeProvider());
        var key = QueryKey.Create("cancel-me");

        var fetcherStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> Fetcher(CancellationToken ct)
        {
            fetcherStarted.TrySetResult();
            return Task.Run(async () =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return 1;
            }, ct);
        }

        var observer = client.Watch<int>(key);
        var fetch = client.FetchAsync(key, Fetcher);

        await fetcherStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); // fetch is genuinely in flight

        observer.Dispose(); // last observer leaves → opted-in cancellation fires

        await fetch.WaitAsync(TimeSpan.FromSeconds(5)); // the shared cycle unwinds cleanly

        var state = client.GetQueryData<int>(key);
        Assert.False(state.IsSuccess);
        Assert.False(state.HasData);
    }

    [Fact]
    public async Task Runs_in_flight_fetch_to_completion_by_default_when_observer_leaves()
    {
        using var client = new QueryClient(new QueryClientOptions { DefaultRetryCount = 0 }, new FakeTimeProvider());
        var key = QueryKey.Create("keep-going");

        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = client.Watch<int>(key);
        var fetch = client.FetchAsync(key, _ => gate.Task);

        observer.Dispose();        // last observer leaves, but default policy keeps the fetch alive
        gate.SetResult(42);

        var result = await fetch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Data);
        Assert.Equal(42, client.GetQueryData<int>(key).Data);
    }

    [Fact]
    public async Task Per_query_option_overrides_the_client_default()
    {
        // Client default is "don't cancel"; a single query opts in.
        using var client = new QueryClient(new QueryClientOptions { DefaultRetryCount = 0 }, new FakeTimeProvider());
        var key = QueryKey.Create("opt-in");

        var fetcherStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> Fetcher(CancellationToken ct)
        {
            fetcherStarted.TrySetResult();
            return Task.Run(async () =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return 1;
            }, ct);
        }

        var observer = client.Watch<int>(key);
        var fetch = client.FetchAsync(key, Fetcher, new QueryOptions { CancelInFlightWhenUnobserved = true });

        await fetcherStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        observer.Dispose();
        await fetch.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(client.GetQueryData<int>(key).IsSuccess);
    }
}
