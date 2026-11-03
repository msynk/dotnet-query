using DotNetQuery;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Tests;

public class QueryFilterTests
{
    private readonly FakeTimeProvider _time = new();

    private QueryClient CreateClient() =>
        new(TestUtil.NoRetry(o => o.DefaultStaleTime = TimeSpan.FromMinutes(1)), _time);

    private static Func<CancellationToken, Task<int>> Counting(Dictionary<string, int> calls, string name) =>
        _ =>
        {
            lock (calls)
            {
                calls[name] = calls.GetValueOrDefault(name) + 1;
                return Task.FromResult(calls[name]);
            }
        };

    [Fact]
    public async Task FindAll_filters_by_key_activity_staleness_status_and_predicate()
    {
        using var client = CreateClient();
        var calls = new Dictionary<string, int>();
        await client.FetchAsync(QueryKey.Create("todos", 1), Counting(calls, "t1"));
        await client.FetchAsync(QueryKey.Create("todos", 2), Counting(calls, "t2"), new QueryOptions { StaleTime = TimeSpan.Zero });
        await client.FetchAsync(QueryKey.Create("todos"), Counting(calls, "list"));
        await client.FetchAsync<int>(QueryKey.Create("users", 1), _ => throw new InvalidOperationException());
        using var observer = client.Watch<int>(QueryKey.Create("todos", 1));

        Keys(client.Cache.FindAll(QueryFilter.ForKey(QueryKey.Create("todos"))))
            .Should(["[\"todos\"]", "[\"todos\", 1]", "[\"todos\", 2]"]);
        Keys(client.Cache.FindAll(QueryFilter.ForKey(QueryKey.Create("todos"), exact: true))).Should(["[\"todos\"]"]);
        Keys(client.Cache.FindAll(new QueryFilter { Type = QueryTypeFilter.Active })).Should(["[\"todos\", 1]"]);
        Keys(client.Cache.FindAll(new QueryFilter { Key = QueryKey.Create("todos"), Type = QueryTypeFilter.Inactive }))
            .Should(["[\"todos\"]", "[\"todos\", 2]"]);
        Keys(client.Cache.FindAll(new QueryFilter { Key = QueryKey.Create("todos"), Stale = true })).Should(["[\"todos\", 2]"]);
        Keys(client.Cache.FindAll(new QueryFilter { Status = QueryStatus.Error })).Should(["[\"users\", 1]"]);
        Keys(client.Cache.FindAll(new QueryFilter { Predicate = q => q.DataTypeName == "Int32" && q.FetchCount == 1 && q.Key.Count == 1 }))
            .Should(["[\"todos\"]"]);
        Assert.Equal(4, client.Cache.FindAll().Count);
    }

    [Fact]
    public void QueryFilter_matches_descriptors_the_same_way_as_entries()
    {
        var descriptor = new QueryDescriptor
        {
            Key = QueryKey.Create("a", 1),
            Status = QueryStatus.Refetching,
            DataTypeName = "Int32",
            HasData = true,
            IsStale = true,
            ObserverCount = 1,
            IsActive = true,
            FailureCount = 0,
            FetchCount = 2,
            LastActivityAt = DateTimeOffset.UnixEpoch,
            CacheTime = TimeSpan.Zero,
        };

        Assert.True(new QueryFilter { Key = QueryKey.Create("a"), Type = QueryTypeFilter.Active, Stale = true, Fetching = true }.Matches(descriptor));
        Assert.False(new QueryFilter { Fetching = false }.Matches(descriptor));
        Assert.False(new QueryFilter { Type = QueryTypeFilter.Inactive }.Matches(descriptor));
        Assert.False(QueryFilter.ForKey(QueryKey.Create("a"), exact: true).Matches(descriptor));
    }

    [Theory]
    [InlineData(RefetchType.None, 1, 1)]
    [InlineData(RefetchType.Active, 2, 1)]
    [InlineData(RefetchType.Inactive, 1, 2)]
    [InlineData(RefetchType.All, 2, 2)]
    public async Task Invalidate_by_filter_marks_stale_and_refetches_per_refetch_type(RefetchType refetchType, int activeCalls, int inactiveCalls)
    {
        using var client = CreateClient();
        var calls = new Dictionary<string, int>();
        var active = QueryKey.Create("todos", "active");
        var inactive = QueryKey.Create("todos", "inactive");
        using var observer = client.Watch<int>(active);
        await client.FetchAsync(active, Counting(calls, "active"));
        await client.FetchAsync(inactive, Counting(calls, "inactive"));

        await client.InvalidateQueriesAsync(QueryFilter.ForKey(QueryKey.Create("todos")), refetchType);

        Assert.Equal(activeCalls, calls["active"]);
        Assert.Equal(inactiveCalls, calls["inactive"]);
        if (refetchType is RefetchType.None)
        {
            Assert.True(client.Cache.Describe(active)!.IsStale);
            Assert.True(client.Cache.Describe(inactive)!.IsStale);
        }
    }

    [Fact]
    public async Task Invalidation_reports_one_event_for_key_and_filter_paths()
    {
        using var client = CreateClient();
        var events = new List<QueryInvalidatedEvent>();
        client.DiagnosticEvent += e => { if (e is QueryInvalidatedEvent i) { events.Add(i); } };
        await client.FetchAsync(QueryKey.Create("x", 1), _ => Task.FromResult(1));

        await client.InvalidateQueriesAsync(QueryKey.Create("x"));
        await client.InvalidateQueriesAsync(new QueryFilter { Key = QueryKey.Create("x") }, RefetchType.None);
        await client.InvalidateQueriesAsync(new QueryFilter { Key = QueryKey.Create("nothing") });

        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal([QueryKey.Create("x", 1)], e.Keys));
    }

    [Fact]
    public async Task Refetch_by_filter_refetches_matching_queries_but_skips_disabled_ones()
    {
        using var client = CreateClient();
        var calls = new Dictionary<string, int>();
        await client.FetchAsync(QueryKey.Create("a", 1), Counting(calls, "a1"));
        await client.FetchAsync(QueryKey.Create("a", 2), Counting(calls, "a2"));
        await client.FetchAsync(QueryKey.Create("a", 3), Counting(calls, "a3"), new QueryOptions { Enabled = false });
        await client.FetchAsync(QueryKey.Create("b"), Counting(calls, "b"));

        await client.RefetchQueriesAsync(QueryFilter.ForKey(QueryKey.Create("a")));

        Assert.Equal(2, calls["a1"]);
        Assert.Equal(2, calls["a2"]);
        Assert.False(calls.ContainsKey("a3"));
        Assert.Equal(1, calls["b"]);
        Assert.Equal(2, client.GetQueryData<int>(QueryKey.Create("a", 1)).Data);
    }

    [Fact]
    public async Task Refetch_by_filter_supersedes_a_fetch_in_flight()
    {
        using var client = CreateClient();
        var key = QueryKey.Create("slow");
        var started = TestUtil.NewSignal();
        var never = TestUtil.NewSignal<int>();
        var calls = 0;
        async Task<int> Fetcher(CancellationToken ct)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.TrySetResult();
                return await never.Task.WaitAsync(ct);
            }

            return 42;
        }

        var first = client.FetchAsync(key, Fetcher);
        await started.Task;

        await client.RefetchQueriesAsync(QueryFilter.ForKey(key));

        // The in-flight fetch was replaced, not joined, and its caller receives the refetch's result.
        Assert.Equal(42, (await first).Data);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Cancel_by_filter_restores_previous_state()
    {
        using var client = CreateClient();
        var key = QueryKey.Create("c", 1);
        await client.FetchAsync(key, _ => Task.FromResult(1));
        var started = TestUtil.NewSignal();
        var never = new TaskCompletionSource<int>();
        var refetch = client.FetchAsync(key, TestUtil.Gated(started, never.Task), forceRefetch: true);
        await started.Task;
        Assert.Equal(1, client.CountFetching());

        await client.CancelQueriesAsync(new QueryFilter { Fetching = true });

        var state = client.GetQueryData<int>(key);
        Assert.Equal(QueryStatus.Success, state.Status);
        Assert.Equal(1, state.Data);
        Assert.Equal(0, client.CountFetching());
        Assert.Equal(1, (await refetch).Data);
    }

    [Fact]
    public async Task Reset_clears_data_refetches_active_queries_and_leaves_inactive_idle()
    {
        using var client = CreateClient();
        var calls = new Dictionary<string, int>();
        var active = QueryKey.Create("r", "active");
        var inactive = QueryKey.Create("r", "inactive");
        using var observer = client.Watch<int>(active);
        var statuses = new List<QueryStatus>();
        observer.StateChanged += s => { lock (statuses) { statuses.Add(s.Status); } };
        await client.FetchAsync(active, Counting(calls, "active"));
        await client.FetchAsync(inactive, Counting(calls, "inactive"));

        await client.ResetQueriesAsync(QueryFilter.ForKey(QueryKey.Create("r")));

        Assert.Equal(2, calls["active"]);
        Assert.Equal(2, client.GetQueryData<int>(active).Data);
        Assert.Equal(1, calls["inactive"]);
        var reset = client.GetQueryData<int>(inactive);
        Assert.Equal(QueryStatus.Idle, reset.Status);
        Assert.False(reset.HasData);

        // The observer saw the reset (Idle, no data) before the refetch started.
        lock (statuses)
        {
            Assert.Contains(QueryStatus.Idle, statuses);
        }
    }

    [Fact]
    public async Task Reset_cancels_a_fetch_in_flight_without_restoring_old_data()
    {
        using var client = CreateClient();
        var key = QueryKey.Create("reset-inflight");
        await client.FetchAsync(key, _ => Task.FromResult(1));
        var started = TestUtil.NewSignal();
        var never = new TaskCompletionSource<int>();
        _ = client.FetchAsync(key, TestUtil.Gated(started, never.Task), forceRefetch: true);
        await started.Task;

        await client.ResetQueriesAsync(QueryFilter.ForKey(key));

        var state = client.GetQueryData<int>(key);
        Assert.Equal(QueryStatus.Idle, state.Status);
        Assert.False(state.HasData);
    }

    [Fact]
    public async Task Remove_by_filter_returns_the_count_and_keeps_other_queries()
    {
        using var client = CreateClient();
        await client.FetchAsync(QueryKey.Create("tenant", 1, "a"), _ => Task.FromResult(1));
        await client.FetchAsync(QueryKey.Create("tenant", 1, "b"), _ => Task.FromResult(2));
        await client.FetchAsync(QueryKey.Create("tenant", 2, "a"), _ => Task.FromResult(3));
        var removedEvents = 0;
        client.Cache.Changed += c => { if (c.Kind is QueryCacheChangeKind.Removed) { removedEvents++; } };

        var removed = client.RemoveQueries(QueryFilter.ForKey(QueryKey.Create("tenant", 1)));

        Assert.Equal(2, removed);
        Assert.Equal(2, removedEvents);
        Assert.Equal([QueryKey.Create("tenant", 2, "a")], client.Cache.Keys);
        Assert.Equal(1, client.RemoveQueries());
        Assert.Equal(0, client.Cache.Count);
    }

    private static List<string> Keys(IReadOnlyList<QueryDescriptor> descriptors) =>
        descriptors.Select(d => d.Key.ToString()).Order(StringComparer.Ordinal).ToList();
}

internal static class KeyListAssertions
{
    public static void Should(this List<string> actual, string[] expected) =>
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
}
