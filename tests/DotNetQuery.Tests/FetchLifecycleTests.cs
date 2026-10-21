using DotNetQuery;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static DotNetQuery.Tests.TestUtil;

namespace DotNetQuery.Tests;

/// <summary>Fetch-cycle semantics: loading transitions, cancellation, supersession, isolation, and GC races.</summary>
public class FetchLifecycleTests
{
    [Fact]
    public async Task Loading_state_is_visible_synchronously_when_a_fetch_starts()
    {
        using var client = new QueryClient(NoRetry(), new FakeTimeProvider());
        var key = QueryKey.Create("sync-loading");
        var release = NewSignal<int>();

        var fetch = client.FetchAsync(key, _ => release.Task);

        // No scheduler hop: the very next read (e.g. the first render) already sees Loading.
        Assert.Equal(QueryStatus.Loading, client.GetQueryData<int>(key).Status);

        release.SetResult(1);
        Assert.Equal(1, (await fetch).Data);
    }

    [Fact]
    public async Task Observers_receive_every_transition_in_order()
    {
        using var client = new QueryClient(NoRetry(), new FakeTimeProvider());
        var key = QueryKey.Create("ordered");
        var seen = new List<QueryStatus>();
        using var observer = client.Watch<int>(key);
        observer.StateChanged += s => { lock (seen) { seen.Add(s.Status); } };

        await client.FetchAsync(key, _ => Task.FromResult(1));
        await client.FetchAsync(key, _ => Task.FromResult(2), forceRefetch: true);

        lock (seen)
        {
            Assert.Equal([QueryStatus.Loading, QueryStatus.Success, QueryStatus.Refetching, QueryStatus.Success], seen);
        }
    }

    [Fact]
    public async Task Cancelling_an_initial_fetch_restores_idle_instead_of_leaving_it_loading()
    {
        using var client = new QueryClient(NoRetry(), new FakeTimeProvider());
        var key = QueryKey.Create("cancel-initial");
        var started = NewSignal();

        var fetch = client.FetchAsync(key, Gated(started, NewSignal<int>().Task));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(client.GetQueryData<int>(key).IsLoading);

        await client.CancelQueriesAsync(key);
        var result = await fetch.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(QueryStatus.Idle, result.Status);
        Assert.False(result.IsFetching);
    }

    [Fact]
    public async Task Cancelling_a_refetch_restores_the_previous_success_and_keeps_data()
    {
        var time = new FakeTimeProvider();
        using var client = new QueryClient(NoRetry(), time);
        var key = QueryKey.Create("cancel-refetch");
        await client.FetchAsync(key, _ => Task.FromResult(1));

        var started = NewSignal();
        var refetch = client.FetchAsync(key, Gated(started, NewSignal<int>().Task), forceRefetch: true);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(QueryStatus.Refetching, client.GetQueryData<int>(key).Status);

        await client.CancelQueriesAsync(key);
        var result = await refetch.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(QueryStatus.Success, result.Status);
        Assert.Equal(1, result.Data);
    }

    [Fact]
    public async Task Cancelled_fetch_raises_a_cancelled_event()
    {
        using var client = new QueryClient(NoRetry(), new FakeTimeProvider());
        var key = QueryKey.Create("cancel-event");
        var events = new List<DotNetQueryEvent>();
        client.DiagnosticEvent += e => { lock (events) { events.Add(e); } };
        var started = NewSignal();

        var fetch = client.FetchAsync(key, Gated(started, NewSignal<int>().Task));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await client.CancelQueriesAsync(key);
        await fetch;

        lock (events)
        {
            Assert.Contains(events, e => e is QueryCancelledEvent c && c.Key == key);
            Assert.DoesNotContain(events, e => e is QueryFailedEvent);
        }
    }

    [Fact]
    public async Task Invalidation_during_a_fetch_supersedes_it_and_waiters_get_the_fresh_result()
    {
        using var client = new QueryClient(NoRetry(), new FakeTimeProvider());
        var key = QueryKey.Create("supersede");
        var calls = 0;
        var firstStarted = NewSignal();
        var firstRelease = NewSignal<string>();

        Task<string> Fetcher(CancellationToken ct)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstStarted.TrySetResult();
                return firstRelease.Task; // ignores the token, like a fetcher that can't be aborted
            }

            return Task.FromResult("fresh");
        }

        using var observer = client.Watch<string>(key);
        var waiter = client.FetchAsync(key, Fetcher);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // A mutation committed after the first request started: its response is already outdated.
        await client.InvalidateQueriesAsync(key);
        firstRelease.SetResult("outdated");

        var result = await waiter.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("fresh", result.Data);
        Assert.Equal("fresh", client.GetQueryData<string>(key).Data);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Concurrent_trigger_refetches_still_deduplicate()
    {
        var time = new FakeTimeProvider();
        var focus = new FocusManager();
        using var client = new QueryClient(NoRetry(), time, focusManager: focus);
        var key = QueryKey.Create("dedupe-trigger");
        var calls = 0;
        var release = NewSignal<int>();
        using var observer = client.Watch<int>(key);

        var fetch = client.FetchAsync(key, _ => { Interlocked.Increment(ref calls); return release.Task; });
        focus.Signal(); // background triggers join the in-flight fetch instead of cancelling it
        focus.Signal();
        release.SetResult(1);

        Assert.Equal(1, (await fetch).Data);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_throwing_observer_does_not_fail_or_retry_the_fetch()
    {
        using var client = new QueryClient(NoRetry(o => o.DefaultRetryCount = 3), new FakeTimeProvider());
        var key = QueryKey.Create("bad-observer");
        var calls = 0;
        var observerErrors = new List<QueryObserverErrorEvent>();
        client.DiagnosticEvent += e => { if (e is QueryObserverErrorEvent o) { lock (observerErrors) { observerErrors.Add(o); } } };

        using var observer = client.Watch<int>(key);
        observer.StateChanged += s =>
        {
            if (s.IsSuccess)
            {
                throw new InvalidOperationException("render failed");
            }
        };
        var healthy = new List<QueryStatus>();
        observer.StateChanged += s => healthy.Add(s.Status);

        var result = await client.FetchAsync(key, _ => { Interlocked.Increment(ref calls); return Task.FromResult(9); });

        Assert.True(result.IsSuccess);
        Assert.Equal(1, calls);
        Assert.Contains(QueryStatus.Success, healthy); // the second handler still ran
        lock (observerErrors)
        {
            Assert.Single(observerErrors);
        }
    }

    [Fact]
    public async Task A_throwing_diagnostics_handler_does_not_fail_the_fetch()
    {
        using var client = new QueryClient(NoRetry(), new FakeTimeProvider());
        client.DiagnosticEvent += _ => throw new InvalidOperationException("logger exploded");

        var result = await client.FetchAsync(QueryKey.Create("bad-logger"), _ => Task.FromResult(3));

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Data);
    }

    [Fact]
    public async Task Successful_fetch_with_null_data_is_a_success()
    {
        using var client = new QueryClient(NoRetry(), new FakeTimeProvider());

        var result = await client.FetchAsync<string?>(QueryKey.Create("nullable"), _ => Task.FromResult<string?>(null));

        Assert.True(result.IsSuccess);
        Assert.True(result.HasData);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task Infinite_stale_time_never_goes_stale_until_invalidated()
    {
        var time = new FakeTimeProvider();
        using var client = new QueryClient(NoRetry(o => o.DefaultStaleTime = Timeout.InfiniteTimeSpan), time);
        var key = QueryKey.Create("forever");
        var calls = 0;
        Task<int> Fetcher(CancellationToken _) => Task.FromResult(Interlocked.Increment(ref calls));

        await client.FetchAsync(key, Fetcher);
        time.Advance(TimeSpan.FromDays(365));
        await client.FetchAsync(key, Fetcher);
        Assert.Equal(1, calls);

        await client.InvalidateQueriesAsync(key);
        await client.FetchAsync(key, Fetcher);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Error_state_is_treated_as_stale_so_the_next_fetch_retries()
    {
        var time = new FakeTimeProvider();
        using var client = new QueryClient(NoRetry(o => o.DefaultStaleTime = TimeSpan.FromHours(1)), time);
        var key = QueryKey.Create("retry-on-mount");
        await client.FetchAsync(key, _ => Task.FromResult(1));
        await client.FetchAsync<int>(key, _ => throw new InvalidOperationException("down"), forceRefetch: true);
        Assert.True(client.GetQueryData<int>(key).IsRefetchError);

        var result = await client.FetchAsync(key, _ => Task.FromResult(2));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Data);
    }

    [Fact]
    public async Task Garbage_collection_skips_entries_with_an_in_flight_fetch()
    {
        var time = new FakeTimeProvider();
        using var client = new QueryClient(NoRetry(o => o.DefaultCacheTime = TimeSpan.Zero), time);
        var key = QueryKey.Create("busy");
        var release = NewSignal<int>();

        var fetch = client.FetchAsync(key, _ => release.Task);
        time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(0, client.Cache.CollectGarbage());
        release.SetResult(5);
        Assert.Equal(5, (await fetch).Data);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, client.Cache.CollectGarbage());
    }

    [Fact]
    public async Task Collected_entry_is_transparently_recreated()
    {
        var time = new FakeTimeProvider();
        using var client = new QueryClient(NoRetry(o => o.DefaultCacheTime = TimeSpan.Zero), time);
        var key = QueryKey.Create("recreated");
        await client.FetchAsync(key, _ => Task.FromResult(1));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, client.Cache.CollectGarbage());

        using var observer = client.Watch<int>(key);
        await client.FetchAsync(key, _ => Task.FromResult(2));

        Assert.Equal(2, observer.CurrentState.Data);
    }

    [Fact]
    public async Task Infinite_cache_time_is_never_collected()
    {
        var time = new FakeTimeProvider();
        using var client = new QueryClient(NoRetry(o => o.DefaultCacheTime = Timeout.InfiniteTimeSpan), time);
        await client.FetchAsync(QueryKey.Create("keep"), _ => Task.FromResult(1));

        time.Advance(TimeSpan.FromDays(3650));

        Assert.Equal(0, client.Cache.CollectGarbage());
    }

    [Fact]
    public async Task Disabling_a_query_stops_its_refetch_interval()
    {
        var time = new FakeTimeProvider();
        using var client = new QueryClient(NoRetry(), time);
        var key = QueryKey.Create("interval-toggle");
        var calls = 0;
        Task<int> Fetcher(CancellationToken _) => Task.FromResult(Interlocked.Increment(ref calls));
        using var observer = client.Watch<int>(key);

        await client.FetchAsync(key, Fetcher, new QueryOptions { RefetchInterval = TimeSpan.FromSeconds(10) });
        await client.FetchAsync(key, Fetcher, new QueryOptions { RefetchInterval = TimeSpan.FromSeconds(10), Enabled = false });

        time.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(50);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Invalidation_does_not_refetch_disabled_queries()
    {
        using var client = new QueryClient(NoRetry(), new FakeTimeProvider());
        var key = QueryKey.Create("disabled-invalidate");
        var calls = 0;
        Task<int> Fetcher(CancellationToken _) => Task.FromResult(Interlocked.Increment(ref calls));
        using var observer = client.Watch<int>(key);
        await client.FetchAsync(key, Fetcher);
        await client.FetchAsync(key, Fetcher, new QueryOptions { Enabled = false });

        await client.InvalidateQueriesAsync(key);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SetQueryData_updater_is_atomic_under_contention()
    {
        using var client = new QueryClient(NoRetry(), new FakeTimeProvider());
        var key = QueryKey.Create("counter");
        client.SetQueryData(key, 0);

        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                client.SetQueryData<int>(key, current => current + 1);
            }
        })));

        Assert.Equal(6400, client.GetQueryData<int>(key).Data);
    }

    [Fact]
    public void SetQueryData_updater_receives_default_when_empty()
    {
        using var client = new QueryClient(NoRetry(), new FakeTimeProvider());

        var result = client.SetQueryData<List<int>>(QueryKey.Create("list"), current => [.. current ?? [], 1]);

        Assert.Equal([1], result);
    }

    [Fact]
    public void Hydrate_applies_only_newer_data_and_keeps_its_timestamp()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        using var client = new QueryClient(NoRetry(), time);
        var key = QueryKey.Create("hydrate");
        var serverTime = time.GetUtcNow().AddMinutes(-2);

        Assert.True(client.HydrateQueryData(key, "server", serverTime));
        Assert.Equal(serverTime, client.GetQueryData<string>(key).DataUpdatedAt);

        Assert.False(client.HydrateQueryData(key, "older", serverTime.AddMinutes(-1)));
        Assert.Equal("server", client.GetQueryData<string>(key).Data);

        Assert.True(client.HydrateQueryData(key, "newer", serverTime.AddMinutes(1)));
        Assert.Equal("newer", client.GetQueryData<string>(key).Data);
    }

    [Fact]
    public async Task Hydrated_stale_data_is_refetched_but_fresh_data_is_served()
    {
        var time = new FakeTimeProvider();
        using var client = new QueryClient(NoRetry(o => o.DefaultStaleTime = TimeSpan.FromMinutes(1)), time);
        var calls = 0;
        Task<string> Fetcher(CancellationToken _) { Interlocked.Increment(ref calls); return Task.FromResult("network"); }

        client.HydrateQueryData(QueryKey.Create("fresh"), "hydrated", time.GetUtcNow().AddSeconds(-10));
        client.HydrateQueryData(QueryKey.Create("stale"), "hydrated", time.GetUtcNow().AddMinutes(-10));

        Assert.Equal("hydrated", (await client.FetchAsync(QueryKey.Create("fresh"), Fetcher)).Data);
        Assert.Equal("network", (await client.FetchAsync(QueryKey.Create("stale"), Fetcher)).Data);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SetQueryData_during_a_fetch_reports_refetching_until_it_settles()
    {
        using var client = new QueryClient(NoRetry(), new FakeTimeProvider());
        var key = QueryKey.Create("set-during-fetch");
        var release = NewSignal<int>();
        var fetch = client.FetchAsync(key, _ => release.Task);

        client.SetQueryData(key, 7);
        Assert.Equal(QueryStatus.Refetching, client.GetQueryData<int>(key).Status);

        release.SetResult(8);
        Assert.Equal(8, (await fetch).Data);
    }

    [Fact]
    public async Task Cache_changed_event_reports_added_data_and_removed()
    {
        using var client = new QueryClient(NoRetry(), new FakeTimeProvider());
        var key = QueryKey.Create("changes");
        var changes = new List<QueryCacheChangeKind>();
        client.Cache.Changed += c => { if (c.Key == key) { lock (changes) { changes.Add(c.Kind); } } };

        await client.FetchAsync(key, _ => Task.FromResult(1));
        client.RemoveQuery(key);

        lock (changes)
        {
            Assert.Equal(QueryCacheChangeKind.Added, changes[0]);
            Assert.Contains(QueryCacheChangeKind.StateChanged, changes);
            Assert.Contains(QueryCacheChangeKind.DataUpdated, changes);
            Assert.Equal(QueryCacheChangeKind.Removed, changes[^1]);
        }
    }

    [Fact]
    public async Task ThrowOnError_rethrows_the_original_exception_type()
    {
        using var client = new QueryClient(NoRetry(o => o.DefaultThrowOnError = true), new FakeTimeProvider());

        await Assert.ThrowsAsync<TimeoutException>(() =>
            client.FetchAsync<int>(QueryKey.Create("throws"), _ => throw new TimeoutException()));
    }

    [Fact]
    public async Task RefetchQuery_never_throws_for_fetch_failures()
    {
        using var client = new QueryClient(NoRetry(o => o.DefaultThrowOnError = true), new FakeTimeProvider());
        var key = QueryKey.Create("refetch-no-throw");
        await client.PrefetchAsync<int>(key, _ => throw new InvalidOperationException());

        await client.RefetchQueryAsync(key);

        Assert.True(client.GetQueryData<int>(key).IsError);
    }

    [Fact]
    public async Task Retry_state_reports_failure_count_while_retrying()
    {
        using var client = new QueryClient(NoRetry(o => o.DefaultRetryCount = 2), new FakeTimeProvider());
        var key = QueryKey.Create("retrying");
        var failureCounts = new List<int>();
        using var observer = client.Watch<int>(key);
        observer.StateChanged += s => { lock (failureCounts) { failureCounts.Add(s.FailureCount); } };
        var calls = 0;

        var result = await client.FetchAsync(key, _ =>
            Interlocked.Increment(ref calls) < 3 ? throw new InvalidOperationException() : Task.FromResult(1));

        Assert.True(result.IsSuccess);
        lock (failureCounts)
        {
            Assert.Contains(1, failureCounts);
            Assert.Contains(2, failureCounts);
            Assert.Equal(0, failureCounts[^1]);
        }
    }
}
