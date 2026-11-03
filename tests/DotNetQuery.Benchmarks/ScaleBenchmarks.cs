using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using DotNetQuery;
using DotNetQuery.Persistence;
using Microsoft.Extensions.Caching.Memory;

namespace DotNetQuery.Benchmarks;

/// <summary>Replacing a 10,000-item list observed by 100 observers, and updating 100 keys in one transaction.</summary>
[MemoryDiagnoser]
public class LargeUpdateBenchmarks
{
    private QueryClient _client = default!;
    private readonly QueryKey _listKey = QueryKey.Create("large-list");
    private readonly QueryKey[] _keys = Enumerable.Range(0, 100).Select(i => QueryKey.Create("tx", i)).ToArray();
    private List<QueryObserver<List<int>>> _observers = [];
    private List<int> _list = [];
    private int _version;

    [GlobalSetup]
    public void Setup()
    {
        _client = new QueryClient(new QueryClientOptions { GarbageCollectionInterval = Timeout.InfiniteTimeSpan });
        _list = Enumerable.Range(0, 10_000).ToList();
        _client.SetQueryData(_listKey, _list);
        _observers = Enumerable.Range(0, 100).Select(_ => _client.Watch<List<int>>(_listKey)).ToList();
        foreach (var key in _keys)
        {
            _client.SetQueryData(key, 0);
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _observers.ForEach(o => o.Dispose());
        _client.Dispose();
    }

    /// <summary>A refetch that returns a new 10,000-item list, fanned out to 100 observers.</summary>
    [Benchmark]
    public void ReplaceObservedLargeList()
    {
        var next = new List<int>(_list) { [0] = ++_version };
        _client.SetQueryData(_listKey, next);
    }

    /// <summary>One atomic transaction updating 100 keys.</summary>
    [Benchmark]
    public void UpdateHundredKeysInOneTransaction() =>
        _client.SetQueriesData(tx =>
        {
            foreach (var key in _keys)
            {
                tx.Update<int>(key, n => n + 1);
            }
        });
}

/// <summary>Allocation of loading 50 pages of 100 items into a cache-backed infinite query.</summary>
[MemoryDiagnoser]
public class InfiniteQueryMemoryBenchmarks
{
    private static readonly InfiniteQueryOptions<int, int[]> Options = new()
    {
        InitialPageParam = 0,
        GetNextPageParam = (_, _, parameters) => parameters[^1] < 49 ? NextPageParam.Of(parameters[^1] + 1) : NextPageParam.None<int>(),
    };

    private static readonly Func<int, CancellationToken, Task<int[]>> Fetcher =
        (page, _) => Task.FromResult(Enumerable.Range(page * 100, 100).ToArray());

    private int _run;

    [Benchmark]
    public async Task<int> LoadFiftyPages()
    {
        using var client = new QueryClient(new QueryClientOptions { GarbageCollectionInterval = Timeout.InfiniteTimeSpan });
        using var feed = client.CreateInfiniteQuery(QueryKey.Create("feed", _run++), Fetcher, Options);
        var state = await feed.FetchPagesAsync(50);
        return state.PageCount;
    }
}

/// <summary>Persisting and restoring 100 queries through the persistence manager (source-generated JSON).</summary>
[MemoryDiagnoser]
public class PersistenceBenchmarks
{
    private QueryClient _client = default!;
    private QueryPersistenceManager _manager = default!;
    private InMemoryQueryPersister _persister = default!;

    [GlobalSetup]
    public async Task Setup()
    {
        _client = new QueryClient(new QueryClientOptions { GarbageCollectionInterval = Timeout.InfiniteTimeSpan });
        _persister = new InMemoryQueryPersister();
        _manager = new QueryPersistenceManager(_client, _persister).RegisterPrefix(QueryKey.Create("todo"), BenchmarkJson.Default.BenchTodo);
        for (var i = 0; i < 100; i++)
        {
            _client.SetQueryData(QueryKey.Create("todo", i), new BenchTodo(i, $"Todo {i}", i % 2 == 0));
        }

        await _manager.PersistAllAsync();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _manager.Dispose();
        _client.Dispose();
    }

    [Benchmark]
    public Task PersistHundredQueries() => _manager.PersistAllAsync();

    [Benchmark]
    public async Task<int> RestoreHundredQueries()
    {
        using var client = new QueryClient(new QueryClientOptions { GarbageCollectionInterval = Timeout.InfiniteTimeSpan });
        using var manager = new QueryPersistenceManager(client, _persister).RegisterPrefix(QueryKey.Create("todo"), BenchmarkJson.Default.BenchTodo);
        return await manager.RestoreAllAsync();
    }
}

/// <summary>Garbage-collecting 10,000 expired inactive queries, and trimming them to a size bound.</summary>
[MemoryDiagnoser]
public class EvictionBenchmarks
{
    private QueryClient _client = default!;

    [IterationSetup]
    public void Fill()
    {
        _client = new QueryClient(new QueryClientOptions
        {
            GarbageCollectionInterval = Timeout.InfiniteTimeSpan,
            DefaultCacheTime = TimeSpan.Zero,
        });
        for (var i = 0; i < 10_000; i++)
        {
            _client.SetQueryData(QueryKey.Create("expired", i), i);
        }
    }

    [IterationCleanup]
    public void Cleanup() => _client.Dispose();

    [Benchmark]
    public int CollectTenThousandExpired() => _client.Cache.CollectGarbage();

    [Benchmark]
    public int TrimTenThousandToHundred() => _client.Cache.Trim(100);
}

/// <summary>
/// The cost of serving cached data, compared with representative .NET caches: <see cref="MemoryCache"/>
/// (<c>GetOrCreateAsync</c>) and a bare <see cref="ConcurrentDictionary{TKey, TValue}"/> of tasks (the floor - no
/// expiry, no deduplication guarantees, no notifications).
/// </summary>
[MemoryDiagnoser]
public class CacheComparisonBenchmarks
{
    private static readonly Func<CancellationToken, Task<int>> Fetcher = _ => Task.FromResult(42);
    private readonly QueryKey _key = QueryKey.Create("todos", 1);
    private readonly QueryOptions _options = new() { StaleTime = TimeSpan.FromHours(1) };
    private readonly ConcurrentDictionary<string, Task<int>> _dictionary = new();
    private QueryClient _client = default!;
    private MemoryCache _memoryCache = default!;

    [GlobalSetup]
    public async Task Setup()
    {
        _client = new QueryClient();
        await _client.FetchAsync(_key, Fetcher, _options);
        _memoryCache = new MemoryCache(new MemoryCacheOptions());
        await _memoryCache.GetOrCreateAsync("todos:1", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
            return Task.FromResult(42);
        });
        _dictionary["todos:1"] = Task.FromResult(42);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _client.Dispose();
        _memoryCache.Dispose();
    }

    [Benchmark(Baseline = true)]
    public async Task<int> ConcurrentDictionaryHit() => await _dictionary.GetOrAdd("todos:1", static _ => Task.FromResult(42));

    [Benchmark]
    public async Task<int> MemoryCacheHit() =>
        await _memoryCache.GetOrCreateAsync("todos:1", static entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
            return Task.FromResult(42);
        });

    [Benchmark]
    public async Task<int> DotNetQueryHit() => (await _client.FetchAsync(_key, Fetcher, _options)).Data;
}

public sealed record BenchTodo(int Id, string Title, bool Done);

[JsonSerializable(typeof(BenchTodo))]
internal sealed partial class BenchmarkJson : JsonSerializerContext;
