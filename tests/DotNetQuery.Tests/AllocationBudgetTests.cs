using DotNetQuery;
using Xunit;
using Xunit.Abstractions;

namespace DotNetQuery.Tests;

/// <summary>
/// Allocation budgets for the hot paths, so a change that makes them allocate much more fails CI instead of slipping
/// through. Budgets leave two to three times the measured cost (noted next to each), which tolerates runtime
/// differences but catches regressions such as a lost fast path, boxing, or a closure per call. Measured per operation
/// over many iterations, on the current thread (the measured paths complete synchronously).
/// </summary>
[Collection(AllocationCollection.Name)]
public class AllocationBudgetTests(ITestOutputHelper output)
{
    private const int Iterations = 2_000;

    private long Measure(string name, Action action)
    {
        for (var i = 0; i < 200; i++)
        {
            action(); // warm up: JIT, tiering, lazy caches
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Iterations; i++)
        {
            action();
        }

        var perOperation = (GC.GetAllocatedBytesForCurrentThread() - before) / Iterations;
        output.WriteLine($"{name}: {perOperation} B/op");
        return perOperation;
    }

    [Fact]
    public void Reading_cached_data_does_not_allocate_beyond_the_result()
    {
        using var client = new QueryClient(new QueryClientOptions { DefaultStaleTime = TimeSpan.FromMinutes(5) });
        var key = QueryKey.Create("todos", 1);
        client.SetQueryData(key, "cached");

        var bytes = Measure("GetQueryData", () => client.GetQueryData<string>(key));

        Assert.True(bytes <= 32, $"GetQueryData allocated {bytes} B/op"); // measured: 0
    }

    [Fact]
    public async Task A_fresh_cache_hit_stays_on_the_fast_path()
    {
        using var client = new QueryClient(new QueryClientOptions { DefaultStaleTime = TimeSpan.FromMinutes(5) });
        var key = QueryKey.Create("todos", 1);
        Func<CancellationToken, Task<string>> fetcher = _ => Task.FromResult("fetched");
        await client.FetchAsync(key, fetcher);

        var bytes = Measure("FetchAsync (fresh hit)", () =>
        {
            var task = client.FetchAsync(key, fetcher);
            Assert.True(task.IsCompletedSuccessfully);
        });

        Assert.True(bytes <= 96, $"A fresh cache hit allocated {bytes} B/op"); // measured: 32 (was 488 before the fast path)
    }

    [Fact]
    public void Building_and_comparing_keys_is_cheap()
    {
        var reference = QueryKey.Create("todos", "list", 42);

        var create = Measure("QueryKey.Create (3 segments)", () => QueryKey.Create("todos", "list", 42));
        var equals = Measure("QueryKey.Equals + GetHashCode", () => _ = reference.Equals(QueryKey.Create("todos", "list", 42)) && reference.GetHashCode() != 0);

        Assert.True(create <= 320, $"QueryKey.Create allocated {create} B/op"); // measured: 152
        Assert.True(equals <= 320, $"Creating and comparing a key allocated {equals} B/op"); // measured: 152 (all of it the key)
    }

    [Fact]
    public void Writing_data_with_an_observer_has_a_bounded_cost()
    {
        using var client = new QueryClient(new QueryClientOptions { DefaultStaleTime = TimeSpan.FromMinutes(5) });
        var key = QueryKey.Create("counter");
        client.SetQueryData(key, 0);
        using var observer = client.Watch<int>(key);
        var value = 0;

        var bytes = Measure("SetQueryData (1 observer)", () => client.SetQueryData(key, ++value));

        Assert.True(bytes <= 320, $"SetQueryData with one observer allocated {bytes} B/op"); // measured: 120
    }
}

// Allocation measurements are per thread, but parallel test classes add GC noise; run them alone.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AllocationCollection
{
    public const string Name = "Allocation budgets";
}
