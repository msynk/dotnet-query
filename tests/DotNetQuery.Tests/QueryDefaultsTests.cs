using System.Reflection;
using DotNetQuery;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Tests;

public class QueryDefaultsTests
{
    [Fact]
    public async Task Prefix_defaults_layer_between_client_defaults_and_query_options()
    {
        var time = new FakeTimeProvider();
        var options = TestUtil.NoRetry()
            .SetQueryDefaults(QueryKey.Create("ref"), new QueryOptions { StaleTime = TimeSpan.FromHours(1), RetryCount = 0 })
            .SetQueryDefaults(QueryKey.Create("ref", "volatile"), new QueryOptions { StaleTime = TimeSpan.FromSeconds(1) });
        using var client = new QueryClient(options, time);
        var calls = new Dictionary<string, int>();
        Task<int> Count(string name) { calls[name] = calls.GetValueOrDefault(name) + 1; return Task.FromResult(calls[name]); }

        await client.FetchAsync(QueryKey.Create("ref", "countries"), _ => Count("countries"));
        await client.FetchAsync(QueryKey.Create("ref", "volatile", "rates"), _ => Count("rates"));
        await client.FetchAsync(QueryKey.Create("ref", "override"), _ => Count("override"), new QueryOptions { StaleTime = TimeSpan.Zero });
        await client.FetchAsync(QueryKey.Create("other"), _ => Count("other"));
        time.Advance(TimeSpan.FromMinutes(1));

        await client.FetchAsync(QueryKey.Create("ref", "countries"), _ => Count("countries"));             // 1 h: fresh
        await client.FetchAsync(QueryKey.Create("ref", "volatile", "rates"), _ => Count("rates"));          // 1 s: stale
        await client.FetchAsync(QueryKey.Create("ref", "override"), _ => Count("override"), new QueryOptions { StaleTime = TimeSpan.Zero });
        await client.FetchAsync(QueryKey.Create("other"), _ => Count("other"));                             // client default 0

        Assert.Equal(1, calls["countries"]);
        Assert.Equal(2, calls["rates"]);
        Assert.Equal(2, calls["override"]);
        Assert.Equal(2, calls["other"]);
        Assert.Equal(TimeSpan.FromHours(1), client.Cache.Describe(QueryKey.Create("ref", "countries"))!.StaleTime);
    }

    [Fact]
    public void GetQueryDefaults_merges_matching_prefixes_and_replaces_by_prefix()
    {
        var options = new QueryClientOptions()
            .SetQueryDefaults(QueryKey.Create("a"), new QueryOptions { StaleTime = TimeSpan.FromSeconds(1), RetryCount = 5 })
            .SetQueryDefaults(QueryKey.Create("a", "b"), new QueryOptions { StaleTime = TimeSpan.FromSeconds(2) });

        var merged = options.GetQueryDefaults(QueryKey.Create("a", "b", "c"))!;
        Assert.Equal(TimeSpan.FromSeconds(2), merged.StaleTime);
        Assert.Equal(5, merged.RetryCount);
        Assert.Null(options.GetQueryDefaults(QueryKey.Create("z")));

        options.SetQueryDefaults(QueryKey.Create("a"), new QueryOptions { RetryCount = 1 });
        Assert.Equal(1, options.GetQueryDefaults(QueryKey.Create("a", "x"))!.RetryCount);
        Assert.Null(options.GetQueryDefaults(QueryKey.Create("a", "x"))!.StaleTime);
    }

    [Fact]
    public void Merge_covers_every_query_option()
    {
        // Guards against adding a QueryOptions property without layering it.
        var fallback = new QueryOptions();
        foreach (var property in typeof(QueryOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
        {
            property.SetValue(fallback, SampleValue(property.PropertyType));
        }

        var options = new QueryClientOptions().SetQueryDefaults(QueryKey.Create("k"), fallback);

        Assert.Equal(fallback, options.GetQueryDefaults(QueryKey.Create("k")));
        var layered = typeof(QueryClientOptions).Assembly.GetType("DotNetQuery.QueryOptions")!
            .GetMethod("Merge", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [new QueryOptions(), fallback]);
        Assert.Equal(fallback, layered);
    }

    private static readonly RetryDelayStrategy SampleDelay = (_, _) => TimeSpan.FromSeconds(3);
    private static readonly RetryPredicate SamplePredicate = (_, _) => false;
    private static readonly Func<QueryDescriptor, TimeSpan?> SampleInterval = _ => TimeSpan.FromSeconds(9);
    private static readonly IReadOnlyDictionary<string, object?> SampleMeta = new Dictionary<string, object?> { ["k"] = 1 };

    private static object SampleValue(Type type) => (Nullable.GetUnderlyingType(type) ?? type) switch
    {
        var t when t == typeof(TimeSpan) => TimeSpan.FromSeconds(42),
        var t when t == typeof(int) => 7,
        var t when t == typeof(bool) => true,
        var t when t == typeof(NetworkMode) => NetworkMode.OfflineFirst,
        var t when t == typeof(RetryDelayStrategy) => SampleDelay,
        var t when t == typeof(RetryPredicate) => SamplePredicate,
        var t when t == typeof(Func<QueryDescriptor, TimeSpan?>) => SampleInterval,
        var t when t == typeof(IReadOnlyDictionary<string, object?>) => SampleMeta,
        var t => throw new InvalidOperationException($"Add a sample value for {t}."),
    };
}
