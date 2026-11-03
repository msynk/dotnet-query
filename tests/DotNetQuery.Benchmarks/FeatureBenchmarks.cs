using BenchmarkDotNet.Attributes;
using DotNetQuery;

namespace DotNetQuery.Benchmarks;

/// <summary>
/// Cost of structural sharing on a refetch of a 1,000-item list: disabled (store the new list), enabled with
/// equal data (whole list kept), and enabled with one changed item (new list reusing 999 items).
/// </summary>
[MemoryDiagnoser]
public class StructuralSharingBenchmarks
{
    private sealed record Item(int Id, string Title, bool Done);

    private QueryClient _client = default!;
    private readonly QueryKey _key = QueryKey.Create("items");
    private List<Item> _template = default!;

    [Params(false, true)]
    public bool Sharing { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _client = new QueryClient(new QueryClientOptions { DefaultStructuralSharing = Sharing });
        _template = Enumerable.Range(0, 1000).Select(i => new Item(i, $"item {i}", false)).ToList();
        _client.FetchAsync(_key, _ => Task.FromResult(Copy(changeAt: -1))).GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void Cleanup() => _client.Dispose();

    /// <summary>A refetch that returns equal data.</summary>
    [Benchmark(Baseline = true)]
    public Task EqualRefetch() => _client.FetchAsync(_key, _ => Task.FromResult(Copy(changeAt: -1)), forceRefetch: true);

    /// <summary>A refetch where exactly one item changed.</summary>
    [Benchmark]
    public Task OneItemChanged() => _client.FetchAsync(_key, _ => Task.FromResult(Copy(changeAt: 500)), forceRefetch: true);

    private List<Item> Copy(int changeAt)
    {
        var list = new List<Item>(_template.Count);
        foreach (var item in _template)
        {
            list.Add(item.Id == changeAt ? item with { Done = !item.Done } : item with { });
        }

        return list;
    }
}

/// <summary>
/// Notification fan-out through 100 observers: plain observers vs selection observers that only notify when a
/// derived value changes (the change here does not affect the selection, so they stay silent).
/// </summary>
[MemoryDiagnoser]
public class SelectorBenchmarks
{
    private QueryClient _client = default!;
    private readonly QueryKey _key = QueryKey.Create("selected");
    private readonly List<IDisposable> _observers = [];
    private int _value;
    private int _notifications;

    [Params(false, true)]
    public bool Selective { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _client = new QueryClient();
        for (var i = 0; i < 100; i++)
        {
            if (Selective)
            {
                var observer = _client.Watch(_key, (int value) => value >= 0, QueryStateProperties.Data);
                observer.StateChanged += _ => _notifications++;
                _observers.Add(observer);
            }
            else
            {
                var observer = _client.Watch<int>(_key);
                observer.StateChanged += _ => _notifications++;
                _observers.Add(observer);
            }
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _observers.ForEach(o => o.Dispose());
        _client.Dispose();
    }

    [Benchmark]
    public void Write() => _client.SetQueryData(_key, _value++);
}

/// <summary>Atomic multi-query writes vs the same writes done one by one.</summary>
[MemoryDiagnoser]
public class TransactionBenchmarks
{
    private QueryClient _client = default!;
    private readonly QueryKey[] _keys = Enumerable.Range(0, 4).Select(i => QueryKey.Create("tx", i)).ToArray();

    [GlobalSetup]
    public void Setup()
    {
        _client = new QueryClient();
        foreach (var key in _keys)
        {
            _client.SetQueryData(key, 0);
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _client.Dispose();

    [Benchmark(Baseline = true)]
    public void FourSeparateUpdates()
    {
        foreach (var key in _keys)
        {
            _client.SetQueryData<int>(key, n => n + 1);
        }
    }

    [Benchmark]
    public QueryDataCommit OneTransaction() => _client.SetQueriesData(tx =>
    {
        foreach (var key in _keys)
        {
            tx.Update<int>(key, n => n + 1);
        }
    });
}

/// <summary>Allocation cost of caching 1,000 distinct queries (memory per cached entry, divided by 1,000).</summary>
[MemoryDiagnoser]
public class CacheFootprintBenchmarks
{
    [Benchmark]
    public int CacheThousandEntries()
    {
        using var client = new QueryClient(new QueryClientOptions { GarbageCollectionInterval = Timeout.InfiniteTimeSpan });
        for (var i = 0; i < 1000; i++)
        {
            client.SetQueryData(QueryKey.Create("entity", i), i);
        }

        return client.Cache.Count;
    }

    [Benchmark]
    public int FilterThousandEntries()
    {
        using var client = new QueryClient(new QueryClientOptions { GarbageCollectionInterval = Timeout.InfiniteTimeSpan });
        for (var i = 0; i < 1000; i++)
        {
            client.SetQueryData(QueryKey.Create("entity", i % 10, i), i);
        }

        return client.Cache.FindAll(QueryFilter.ForKey(QueryKey.Create("entity", 3))).Count;
    }
}
