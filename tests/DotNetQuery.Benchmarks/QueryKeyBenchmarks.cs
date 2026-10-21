using BenchmarkDotNet.Attributes;
using DotNetQuery;

namespace DotNetQuery.Benchmarks;

/// <summary>
/// Measures the cost of constructing query keys and the structural equality / hashing that the cache
/// dictionary performs on every lookup.
/// </summary>
[MemoryDiagnoser]
public class QueryKeyBenchmarks
{
    private QueryKey _key = default!;
    private QueryKey _equalKey = default!;
    private QueryKey _prefix = default!;

    [Params(1, 3, 6)]
    public int SegmentCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _key = MakeKey(SegmentCount);
        _equalKey = MakeKey(SegmentCount);
        _prefix = MakeKey(Math.Max(1, SegmentCount - 1));
    }

    [Benchmark]
    public QueryKey Create() => MakeKey(SegmentCount);

    [Benchmark]
    public int GetHashCode_() => _key.GetHashCode();

    [Benchmark]
    public bool Equals_Equal() => _key.Equals(_equalKey);

    [Benchmark]
    public bool IsPrefixOf() => _prefix.IsPrefixOf(_key);

    private static QueryKey MakeKey(int segments)
    {
        var parts = new object?[segments];
        parts[0] = "entity";
        for (var i = 1; i < segments; i++)
        {
            parts[i] = i;
        }

        return QueryKey.Create(parts);
    }
}
