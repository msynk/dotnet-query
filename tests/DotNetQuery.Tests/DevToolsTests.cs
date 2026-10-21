using System.Text.Json;
using DotNetQuery;
using DotNetQuery.DevTools;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Tests;

public class DevToolsTests
{
    private static QueryClient CreateClient(FakeTimeProvider time) => new(
        new QueryClientOptions
        {
            DefaultRetryDelay = RetryPolicy.Constant(TimeSpan.Zero),
            DefaultRetryCount = 0,
            DefaultStaleTime = TimeSpan.FromMinutes(5),
            GarbageCollectionInterval = TimeSpan.FromHours(1),
        },
        time);

    [Fact]
    public async Task Inspect_describes_cached_queries()
    {
        var time = new FakeTimeProvider();
        using var client = CreateClient(time);
        using var devtools = new QueryDevTools(client);

        await client.FetchAsync(QueryKey.Create("users", 1), _ => Task.FromResult("alice"));

        var descriptor = Assert.Single(devtools.InspectQueries());
        Assert.Equal(QueryKey.Create("users", 1), descriptor.Key);
        Assert.Equal(QueryStatus.Success, descriptor.Status);
        Assert.True(descriptor.HasData);
        Assert.Equal("String", descriptor.DataTypeName);
        Assert.Equal(1, descriptor.FetchCount);
    }

    [Fact]
    public async Task Event_log_records_query_lifecycle()
    {
        var time = new FakeTimeProvider();
        using var client = CreateClient(time);
        using var devtools = new QueryDevTools(client);

        await client.FetchAsync(QueryKey.Create("q"), _ => Task.FromResult(1));

        var events = devtools.RecentEvents();
        Assert.Contains(events, e => e is QueryStartedEvent);
        Assert.Contains(events, e => e is QueryCompletedEvent);
    }

    [Fact]
    public async Task Stats_aggregate_activity()
    {
        var time = new FakeTimeProvider();
        using var client = CreateClient(time);
        using var devtools = new QueryDevTools(client);

        await client.FetchAsync(QueryKey.Create("a"), _ => Task.FromResult(1));
        await client.FetchAsync<int>(QueryKey.Create("b"), _ => throw new InvalidOperationException());

        var stats = devtools.GetStats();
        Assert.Equal(2, stats.TotalQueries);
        Assert.Equal(2, stats.FetchesStarted);
        Assert.Equal(1, stats.FetchesSucceeded);
        Assert.Equal(1, stats.FetchesFailed);
        Assert.Equal(1, stats.StatusCounts[QueryStatus.Success]);
        Assert.Equal(1, stats.StatusCounts[QueryStatus.Error]);
    }

    [Fact]
    public async Task Manual_refetch_reloads_query()
    {
        var time = new FakeTimeProvider();
        using var client = CreateClient(time);
        using var devtools = new QueryDevTools(client);
        var key = QueryKey.Create("r");
        var calls = 0;

        await client.FetchAsync(key, _ => { calls++; return Task.FromResult(calls); });
        await devtools.RefetchAsync(key);

        Assert.Equal(2, calls);
        Assert.Equal(2, client.GetQueryData<int>(key).Data);
    }

    [Fact]
    public async Task Changed_fires_on_new_events()
    {
        var time = new FakeTimeProvider();
        using var client = CreateClient(time);
        using var devtools = new QueryDevTools(client);
        var changed = 0;
        devtools.Changed += () => Interlocked.Increment(ref changed);

        await client.FetchAsync(QueryKey.Create("c"), _ => Task.FromResult(1));

        Assert.True(changed >= 2); // at least started + completed
    }

    [Fact]
    public async Task Export_produces_valid_json_with_queries_and_events()
    {
        var time = new FakeTimeProvider();
        using var client = CreateClient(time);
        using var devtools = new QueryDevTools(client);

        await client.FetchAsync(QueryKey.Create("export", 1), _ => Task.FromResult(new { Name = "x" }));

        var json = devtools.ExportJson();
        using var doc = JsonDocument.Parse(json); // throws if invalid

        var root = doc.RootElement;
        Assert.True(root.GetProperty("stats").GetProperty("totalQueries").GetInt32() >= 1);
        Assert.True(root.GetProperty("queries").GetArrayLength() >= 1);
        Assert.True(root.GetProperty("events").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Disposed_devtools_stops_recording()
    {
        var time = new FakeTimeProvider();
        using var client = CreateClient(time);
        var devtools = new QueryDevTools(client);
        devtools.Dispose();

        await client.FetchAsync(QueryKey.Create("d"), _ => Task.FromResult(1));

        Assert.Empty(devtools.RecentEvents());
    }
}
