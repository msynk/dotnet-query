using DotNetQuery;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Tests;

public class EngineSchedulingTests
{
    [Fact]
    public async Task Attempts_slower_than_the_timeout_fail_with_TimeoutException_and_are_retried()
    {
        using var client = new QueryClient(TestUtil.NoRetry());
        var calls = 0;

        var result = await client.FetchAsync<int>(
            QueryKey.Create("slow"),
            async ct =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    await Task.Delay(Timeout.Infinite); // ignores its token: abandoned by the timeout
                }

                return 7;
            },
            new QueryOptions { Timeout = TimeSpan.FromMilliseconds(50), RetryCount = 1 });

        Assert.Equal(7, result.Data);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_timed_out_query_without_retries_reports_the_timeout()
    {
        using var client = new QueryClient(TestUtil.NoRetry());

        var result = await client.FetchAsync<int>(
            QueryKey.Create("too-slow"),
            async ct => { await Task.Delay(Timeout.Infinite, ct); return 1; },
            new QueryOptions { Timeout = TimeSpan.FromMilliseconds(30) });

        Assert.IsType<TimeoutException>(result.Error);
        Assert.True(HttpRetryPolicy.IsTransient(result.Error!));
    }

    [Fact]
    public async Task Concurrency_limit_caps_running_attempts_and_admits_higher_priority_first()
    {
        using var client = new QueryClient(TestUtil.NoRetry(o => o.MaxConcurrentFetches = 1));
        var started = new List<string>();
        var release = TestUtil.NewSignal();
        var running = 0;
        var maxRunning = 0;
        Func<CancellationToken, Task<int>> Fetcher(string name) => async _ =>
        {
            lock (started)
            {
                started.Add(name);
                maxRunning = Math.Max(maxRunning, ++running);
            }

            await release.Task;
            lock (started)
            {
                running--;
            }

            return 1;
        };

        var first = client.FetchAsync(QueryKey.Create("first"), Fetcher("first"));
        await TestUtil.WaitUntilAsync(() => { lock (started) { return started.Count == 1; } });
        var low = client.FetchAsync(QueryKey.Create("low"), Fetcher("low"), new QueryOptions { Priority = 0 });
        var high = client.FetchAsync(QueryKey.Create("high"), Fetcher("high"), new QueryOptions { Priority = 10 });

        // Both cycles are queued on the thread pool; give them time to reach the limiter even on a loaded machine.
        await TestUtil.WaitUntilAsync(() => client.CountFetching() == 3);
        await Task.Delay(250);
        lock (started)
        {
            Assert.Equal(["first"], started); // the others wait for the single slot
        }

        release.SetResult();
        await Task.WhenAll(first, low, high);

        Assert.Equal(["first", "high", "low"], started);
        Assert.Equal(1, maxRunning);
    }

    [Fact]
    public async Task Cancelled_waiters_give_up_their_place_in_the_limiter_queue()
    {
        using var client = new QueryClient(TestUtil.NoRetry(o => o.MaxConcurrentFetches = 1));
        var release = TestUtil.NewSignal<int>();
        var blockerStarted = TestUtil.NewSignal();
        var blocker = client.FetchAsync(QueryKey.Create("blocker"), TestUtil.Gated(blockerStarted, release.Task));
        await blockerStarted.Task; // the blocker holds the only slot
        var waitingKey = QueryKey.Create("waiting");
        var waiting = client.FetchAsync(waitingKey, _ => Task.FromResult(2));
        await Task.Delay(20);

        await client.CancelQueriesAsync(waitingKey);
        var after = client.FetchAsync(QueryKey.Create("after"), _ => Task.FromResult(3));
        release.SetResult(1);

        Assert.Equal(1, (await blocker).Data);
        Assert.Equal(3, (await after).Data);
        Assert.False((await waiting).HasData);
    }

    private sealed class CountingLimiter(int allow) : IFetchRateLimiter
    {
        public int Acquired;
        public int Released;

        public ValueTask<IDisposable?> AcquireAsync(QueryKey key, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref Acquired) > allow)
            {
                var error = new QueryRateLimitedException($"quota exceeded for {key}");
                error.Data[HttpRetryPolicy.RetryAfterDataKey] = TimeSpan.FromSeconds(2);
                throw error;
            }

            return new ValueTask<IDisposable?>(new Permit(this));
        }

        private sealed class Permit(CountingLimiter owner) : IDisposable
        {
            public void Dispose() => Interlocked.Increment(ref owner.Released);
        }
    }

    [Fact]
    public async Task Rate_limiter_permits_wrap_each_attempt_and_rejections_are_fetch_errors()
    {
        var limiter = new CountingLimiter(allow: 1);
        using var client = new QueryClient(TestUtil.NoRetry(o => o.FetchRateLimiter = limiter));

        var allowed = await client.FetchAsync(QueryKey.Create("a"), _ => Task.FromResult(1));
        var rejected = await client.FetchAsync(QueryKey.Create("b"), _ => Task.FromResult(2));

        Assert.Equal(1, allowed.Data);
        Assert.IsType<QueryRateLimitedException>(rejected.Error);
        Assert.Equal(TimeSpan.FromSeconds(2), HttpRetryPolicy.GetRetryAfter(rejected.Error!));
        Assert.Equal(1, limiter.Released);
    }

    private sealed class RecordingInterceptor(string name, List<string> log) : IQueryInterceptor
    {
        public async Task<T> InterceptAsync<T>(QueryInterceptorContext context, Func<CancellationToken, Task<T>> next, CancellationToken cancellationToken)
        {
            lock (log)
            {
                log.Add($"{name}>{context.Attempt}");
            }

            var result = await next(cancellationToken);
            lock (log)
            {
                log.Add($"{name}<{context.Attempt}");
            }

            return result;
        }
    }

    [Fact]
    public async Task Interceptors_wrap_every_attempt_in_registration_order()
    {
        var log = new List<string>();
        var options = TestUtil.NoRetry(o =>
        {
            o.Interceptors.Add(new RecordingInterceptor("outer", log));
            o.Interceptors.Add(new RecordingInterceptor("inner", log));
        });
        using var client = new QueryClient(options);
        var calls = 0;

        await client.FetchAsync<int>(
            QueryKey.Create("intercepted"),
            _ => ++calls == 1 ? throw new TimeoutException() : Task.FromResult(1),
            new QueryOptions { RetryCount = 1 });

        Assert.Equal(["outer>1", "inner>1", "outer>2", "inner>2", "inner<2", "outer<2"], log);
    }

    private sealed record Job(int Progress)
    {
        public bool Done => Progress >= 100;
    }

    [Fact]
    public async Task Adaptive_polling_follows_the_data_and_stops_when_done()
    {
        var time = new FakeTimeProvider();
        using var client = new QueryClient(TestUtil.NoRetry(), time);
        var key = QueryKey.Create("job");
        var progress = 0;
        var options = new QueryOptions
        {
            AdaptiveRefetchInterval = q => q.Data is Job { Done: false } ? TimeSpan.FromSeconds(1) : null,
        };
        using var observer = client.Watch<Job>(key);
        await client.FetchAsync(key, _ => Task.FromResult(new Job(progress += 50)), options);

        time.Advance(TimeSpan.FromSeconds(1));
        await TestUtil.WaitUntilAsync(() => observer.CurrentState.Data?.Progress == 100);
        await Task.Delay(100); // the next interval is computed right after observers are notified

        time.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        Assert.Equal(100, progress); // done: polling stopped
    }

    [Fact]
    public async Task Invalidation_follows_declared_dependencies_transitively()
    {
        var options = TestUtil.NoRetry(o => o.DefaultStaleTime = TimeSpan.FromMinutes(5))
            .AddInvalidationDependency(QueryKey.Create("todos"), QueryKey.Create("stats"))
            .AddInvalidationDependency(QueryKey.Create("stats"), QueryKey.Create("dashboard"))
            .AddInvalidationDependency(QueryKey.Create("dashboard"), QueryKey.Create("todos")); // cycle: must terminate
        using var client = new QueryClient(options);
        var statsCalls = 0;
        using var stats = client.Watch<int>(QueryKey.Create("stats", "open"));
        await client.FetchAsync(QueryKey.Create("stats", "open"), _ => Task.FromResult(++statsCalls));
        await client.FetchAsync(QueryKey.Create("dashboard"), _ => Task.FromResult(0));
        await client.FetchAsync(QueryKey.Create("unrelated"), _ => Task.FromResult(0));
        var events = new List<QueryInvalidatedEvent>();
        client.DiagnosticEvent += e => { if (e is QueryInvalidatedEvent i) { events.Add(i); } };

        await client.InvalidateQueriesAsync(QueryKey.Create("todos", 1));

        Assert.Equal(2, statsCalls); // active dependent refetched
        Assert.True(client.Cache.Describe(QueryKey.Create("dashboard"))!.IsStale);
        Assert.False(client.Cache.Describe(QueryKey.Create("unrelated"))!.IsStale);
        Assert.Equal(2, Assert.Single(events).Keys.Count);
    }

    [Fact]
    public void Inactive_queries_beyond_the_limit_are_evicted_least_recently_used_first()
    {
        var time = new FakeTimeProvider();
        using var client = new QueryClient(TestUtil.NoRetry(o => { o.MaxInactiveQueries = 2; o.DefaultCacheTime = TimeSpan.FromHours(1); }), time);
        for (var i = 0; i < 5; i++)
        {
            client.SetQueryData(QueryKey.Create("item", i), i);
            time.Advance(TimeSpan.FromSeconds(1));
        }

        using var observer = client.Watch<int>(QueryKey.Create("item", 0)); // active: never evicted

        client.Cache.CollectGarbage();

        Assert.Equal(
            ["[\"item\", 0]", "[\"item\", 3]", "[\"item\", 4]"],
            client.Cache.Keys.Select(k => k.ToString()).Order(StringComparer.Ordinal));
        Assert.Equal(2, client.Cache.Trim(0));
        Assert.Single(client.Cache.Keys);
    }

    [Fact]
    public async Task Snapshots_restore_data_status_and_staleness_exactly()
    {
        var time = new FakeTimeProvider();
        using var client = new QueryClient(TestUtil.NoRetry(o => o.DefaultStaleTime = TimeSpan.FromMinutes(1)), time);
        var a = QueryKey.Create("a");
        var b = QueryKey.Create("b");
        await client.FetchAsync(a, _ => Task.FromResult(1));
        await client.InvalidateQueriesAsync(a);
        var before = client.GetQueryData<int>(a).State;
        var snapshot = client.Cache.CreateSnapshot();

        client.SetQueryData(a, 100);
        client.SetQueryData(b, 2);
        using var observer = client.Watch<int>(a);
        var seen = new List<int>();
        observer.StateChanged += s => { lock (seen) { seen.Add(s.Data); } };

        Assert.Equal(1, snapshot.Restore(removeOthers: true));

        Assert.Equal(before, client.GetQueryData<int>(a).State);
        Assert.True(client.Cache.Describe(a)!.IsStale);
        Assert.False(client.Cache.TryGetState<int>(b, out _));
        await TestUtil.WaitUntilAsync(() => { lock (seen) { return seen.Contains(1); } });
        Assert.Equal([a], snapshot.Keys);
    }

    [Fact]
    public async Task Restoring_a_snapshot_mid_fetch_keeps_the_outdated_result_from_landing()
    {
        using var client = new QueryClient(TestUtil.NoRetry());
        var key = QueryKey.Create("racing");
        client.SetQueryData(key, "snapshotted");
        var snapshot = client.Cache.CreateSnapshot(QueryFilter.ForKey(key));
        var release = TestUtil.NewSignal();
        var fetch = client.FetchAsync(key, async _ => { await release.Task; return "outdated"; }, forceRefetch: true);

        snapshot.Restore();
        release.SetResult();
        await fetch;

        Assert.Equal("snapshotted", client.GetQueryData<string>(key).Data);
    }

    [Fact]
    public void Batched_notifications_arrive_once_the_batch_ends_in_order()
    {
        using var client = new QueryClient(TestUtil.NoRetry());
        var key = QueryKey.Create("batched");
        using var observer = client.Watch<int>(key);
        var seen = new List<int>();
        observer.StateChanged += s => seen.Add(s.Data);

        using (QueryNotifications.Batch())
        {
            using (QueryNotifications.Batch())
            {
                client.SetQueryData(key, 1);
            }

            client.SetQueryData(key, 2);
            client.SetQueryData(key, 3);
            Assert.Empty(seen);
            Assert.Equal(3, client.GetQueryData<int>(key).Data); // the cache itself is up to date
        }

        Assert.Equal([1, 2, 3], seen);
    }

    [Fact]
    public async Task FetchAllAsync_fetches_a_dynamic_set_in_parallel_and_keeps_order()
    {
        using var client = new QueryClient(TestUtil.NoRetry());
        var definitions = Enumerable.Range(1, 5)
            .Select(i => new QueryDefinition<int>(QueryKey.Create("n", i), async _ => { await Task.Delay(10 * (6 - i)); return i * i; }))
            .ToList();

        var results = await client.FetchAllAsync(definitions);

        Assert.Equal([1, 4, 9, 16, 25], results.Select(r => r.Data));
    }
}
