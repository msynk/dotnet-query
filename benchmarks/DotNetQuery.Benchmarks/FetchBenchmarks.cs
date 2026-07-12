using BenchmarkDotNet.Attributes;
using DotNetQuery;

namespace DotNetQuery.Benchmarks;

/// <summary>
/// Compares the fast path (serving fresh cached data without invoking the fetcher) against the cold
/// path (creating an entry and running the full fetch cycle) and a forced refetch of an existing entry.
/// </summary>
[MemoryDiagnoser]
public class FetchBenchmarks
{
    private QueryClient _client = default!;
    private readonly QueryKey _warmKey = QueryKey.Create("warm");
    private readonly QueryKey _refetchKey = QueryKey.Create("refetch");
    private readonly QueryOptions _longStale = new() { StaleTime = TimeSpan.FromHours(1) };
    private static readonly Func<CancellationToken, Task<int>> Fetcher = _ => Task.FromResult(42);
    private int _cold;

    [GlobalSetup]
    public async Task Setup()
    {
        _client = new QueryClient();
        // Warm both keys so the cached / forced-refetch paths operate on existing fresh entries.
        await _client.FetchAsync(_warmKey, Fetcher, _longStale);
        await _client.FetchAsync(_refetchKey, Fetcher, _longStale);
    }

    [GlobalCleanup]
    public void Cleanup() => _client.Dispose();

    /// <summary>Fresh cached entry: returns immediately without touching the fetcher.</summary>
    [Benchmark(Baseline = true)]
    public async Task<int> CachedHit()
    {
        var result = await _client.FetchAsync(_warmKey, Fetcher, _longStale);
        return result.Data;
    }

    /// <summary>New key every invocation: exercises entry creation plus the full fetch cycle.</summary>
    [Benchmark]
    public async Task<int> ColdFetch()
    {
        var key = QueryKey.Create("cold", _cold++);
        var result = await _client.FetchAsync(key, Fetcher);
        return result.Data;
    }

    /// <summary>Existing entry, staleness bypassed: runs the fetch cycle again on a live entry.</summary>
    [Benchmark]
    public async Task<int> ForcedRefetch()
    {
        var result = await _client.FetchAsync(_refetchKey, Fetcher, _longStale, forceRefetch: true);
        return result.Data;
    }
}
