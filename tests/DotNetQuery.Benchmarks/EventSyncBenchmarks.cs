using BenchmarkDotNet.Attributes;
using DotNetQuery;
using DotNetQuery.Synchronization;

namespace DotNetQuery.Benchmarks;

/// <summary>
/// Real-time event synchronization: dispatching an event through a <see cref="QueryEventSynchronizer"/> (queue,
/// metadata, duplicate and ordering checks, mapping resolution, cache write), duplicate rejection, an entity upsert into
/// a cache holding many queries, and a burst of invalidations coalesced into one refetch.
/// </summary>
[MemoryDiagnoser]
public class EventSyncBenchmarks
{
    private sealed record Item(int Id, string Title, long Version);

    private sealed record Changed(string Id, Item Item);

    private static readonly QueryKey Items = QueryKey.Create("items");
    private static readonly QueryKey Counter = QueryKey.Create("counter");

    private QueryClient _client = default!;
    private QueryEventSynchronizer _sync = default!;
    private QueryEntity<Item, int> _entity = default!;
    private long _next;

    /// <summary>How many unrelated queries the cache holds (what every key-prefix scan walks).</summary>
    [Params(10, 1_000)]
    public int CachedQueries { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _client = new QueryClient(new QueryClientOptions { GarbageCollectionInterval = Timeout.InfiniteTimeSpan });
        for (var i = 0; i < CachedQueries; i++)
        {
            _client.SetQueryData(QueryKey.Create("unrelated", i), i);
        }

        _client.SetQueryData(Counter, 0L);
        _client.SetQueryData(Items.Append("list"), Enumerable.Range(0, 100).Select(i => new Item(i, $"item {i}", 0)).ToList());
        _client.SetQueryData(Items.Append("by-id", 7), new Item(7, "item 7", 0));
        _entity = QueryEntity.Create<Item, int>(Items, i => i.Id, version: i => i.Version);

        _sync = new QueryEventSynchronizer(_client, options: new QueryEventSyncOptions { DeduplicationCapacity = 100_000 });
        _sync.Map<Changed>("Counted", m => m
            .WithMetadata(e => new QueryEventMetadata { EventId = e.Id })
            .Update<long>(Counter, (n, _) => n + 1));
        _sync.Map<Changed>("Upserted", m => m.Upsert(_entity, e => e.Item));
        _sync.Map<Changed>("Touched", m => m.Invalidate(Items));
        _sync.ProcessAsync("Counted", new Changed("seen", new Item(0, "x", 0))).AsTask().GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _sync.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _client.Dispose();
    }

    /// <summary>One event end to end: queue, metadata, dedup check, mapping, atomic cache update.</summary>
    [Benchmark(Baseline = true)]
    public ValueTask<QueryEventOutcome> Dispatch_Update() =>
        _sync.ProcessAsync("Counted", new Changed((++_next).ToString(System.Globalization.CultureInfo.InvariantCulture), new Item(0, "x", 0)));

    /// <summary>A redelivered event rejected by the deduplication window.</summary>
    [Benchmark]
    public ValueTask<QueryEventOutcome> Dispatch_Duplicate() => _sync.ProcessAsync("Counted", new Changed("seen", new Item(0, "x", 0)));

    /// <summary>An entity upsert: scans the cache for the entity's shapes, replaces it in a 100-item list and its detail.</summary>
    [Benchmark]
    public ValueTask<QueryEventOutcome> Dispatch_Upsert() =>
        _sync.ProcessAsync("Upserted", new Changed("u", new Item(7, "renamed", ++_next)));

    /// <summary>100 invalidation events posted in a burst: coalesced into one invalidation of the prefix.</summary>
    [Benchmark(OperationsPerInvoke = 100)]
    public async Task Burst_100_Invalidations()
    {
        for (var i = 0; i < 99; i++)
        {
            _sync.Post("Touched", new Changed("t", new Item(0, "x", 0)));
        }

        await _sync.ProcessAsync("Touched", new Changed("t", new Item(0, "x", 0)));
    }
}
