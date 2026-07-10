using DotNetQuery;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Core.Tests;

/// <summary>
/// Stress and concurrency tests validating the library's thread-safety guarantees under contention.
/// </summary>
public class ConcurrencyTests
{
    private static QueryClient CreateClient(TimeSpan? staleTime = null) => new(
        new QueryClientOptions
        {
            DefaultRetryDelay = RetryPolicy.Constant(TimeSpan.Zero),
            DefaultRetryCount = 0,
            DefaultStaleTime = staleTime ?? TimeSpan.FromMinutes(10),
            GarbageCollectionInterval = TimeSpan.FromHours(1),
        },
        new FakeTimeProvider());

    [Fact]
    public async Task Highly_concurrent_fetches_of_one_key_deduplicate_to_a_single_call()
    {
        using var client = CreateClient();
        var key = QueryKey.Create("hot");
        var calls = 0;
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = Enumerable.Range(0, 256).Select(_ => Task.Run(async () =>
            await client.FetchAsync(key, _ =>
            {
                Interlocked.Increment(ref calls);
                return gate.Task;
            }))).ToArray();

        await Task.Delay(100); // let all callers reach the dedup point
        gate.SetResult(42);
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, calls); // one network call for 256 concurrent callers
        Assert.All(results, r => Assert.Equal(42, r.Data));
    }

    [Fact]
    public async Task Concurrent_reads_writes_and_removes_across_keys_do_not_corrupt_the_cache()
    {
        using var client = CreateClient();

        var work = Enumerable.Range(0, 4000).Select(i => Task.Run(() =>
        {
            var key = QueryKey.Create("bucket", i % 64);
            client.SetQueryData(key, i);
            _ = client.GetQueryData<int>(key);
            _ = client.Cache.Describe();
            if (i % 11 == 0)
            {
                client.RemoveQuery(key);
            }
        })).ToArray();

        await Task.WhenAll(work); // must complete without throwing
        Assert.True(client.Cache.Count <= 64);
    }

    [Fact]
    public async Task Concurrent_fetches_and_invalidations_stay_consistent()
    {
        using var client = CreateClient(TimeSpan.Zero); // always stale → maximal refetch pressure
        var key = QueryKey.Create("users", 1);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var fetchers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                await client.FetchAsync(key, ct => Task.FromResult(Environment.TickCount));
            }
        }));
        var invalidators = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                await client.InvalidateQueriesAsync(QueryKey.Create("users"));
            }
        }));

        await Task.WhenAll(fetchers.Concat(invalidators)); // no deadlock, no exception
        Assert.True(client.GetQueryData<int>(key).HasData);
    }

    [Fact]
    public async Task Concurrent_observer_churn_with_state_changes_is_safe()
    {
        using var client = CreateClient();
        var key = QueryKey.Create("watched");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var writers = Task.Run(() =>
        {
            var n = 0;
            while (!cts.IsCancellationRequested)
            {
                client.SetQueryData(key, n++);
            }
        });

        var subscribers = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                var observer = client.Watch<int>(key);
                var received = 0;
                observer.StateChanged += _ => Interlocked.Increment(ref received);
                Thread.SpinWait(200);
                observer.Dispose();
            }
        })).ToArray();

        await Task.WhenAll(subscribers.Append(writers));

        // All observers disposed → entry has no lingering subscribers.
        Assert.Equal(0, client.Cache.Describe().Single(d => d.Key == key).ObserverCount);
    }

    [Fact]
    public async Task Concurrent_independent_mutations_return_correct_results()
    {
        using var client = CreateClient();

        // The one-shot MutateAsync creates a fresh Mutation per call, so overlapping invocations are
        // independent — each result reflects its own variables.
        var results = await Task.WhenAll(
            Enumerable.Range(0, 128).Select(i => Task.Run(() =>
                client.MutateAsync<int, int>((v, _) => Task.FromResult(v * 2), i))));

        Assert.Equal(128, results.Length);
        Assert.All(results, r => Assert.True(r.IsSuccess));
        for (var i = 0; i < results.Length; i++)
        {
            Assert.Equal(results[i].State.Variables * 2, results[i].Data);
        }
    }
}
