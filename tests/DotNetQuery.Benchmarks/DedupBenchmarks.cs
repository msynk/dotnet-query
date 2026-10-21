using BenchmarkDotNet.Attributes;
using DotNetQuery;

namespace DotNetQuery.Benchmarks;

/// <summary>
/// Measures request deduplication: many concurrent callers requesting the same key while one fetch is
/// in flight should collapse onto a single shared fetch cycle.
/// </summary>
[MemoryDiagnoser]
public class DedupBenchmarks
{
    private QueryClient _client = default!;
    private readonly QueryKey _key = QueryKey.Create("dedup");
    private TaskCompletionSource<int> _gate = default!;
    private Task<QueryResult<int>>[] _callers = default!;

    [Params(2, 8, 32)]
    public int Fanout { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _client = new QueryClient();
        _callers = new Task<QueryResult<int>>[Fanout];
    }

    [GlobalCleanup]
    public void Cleanup() => _client.Dispose();

    [IterationSetup]
    public void IterationSetup()
    {
        // Fresh gated fetcher and entry per iteration so every fan-out actually deduplicates.
        _gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.RemoveQuery(_key);
    }

    [Benchmark]
    public async Task Fanout_Dedup()
    {
        for (var i = 0; i < Fanout; i++)
        {
            _callers[i] = _client.FetchAsync(_key, _ => _gate.Task);
        }

        _gate.SetResult(1);
        await Task.WhenAll(_callers);
    }
}
