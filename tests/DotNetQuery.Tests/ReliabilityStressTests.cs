using DotNetQuery;
using DotNetQuery.Persistence;
using Xunit;

namespace DotNetQuery.Tests;

/// <summary>
/// Stress and race tests for the reliability scenarios of the improvement plan: many observers, cancellation during
/// retries, disposal during notification, eviction during fetches, hydration races, connectivity flapping during
/// mutations and shutdown during persistence. Each runs many iterations with real thread-pool concurrency.
/// </summary>
public class ReliabilityStressTests
{
    [Fact]
    public async Task Hundreds_of_observers_converge_on_the_final_state_under_concurrent_invalidation_and_refetch()
    {
        using var client = new QueryClient(TestUtil.NoRetry());
        var key = QueryKey.Create("crowd");
        var version = 0;
        Task<int> Fetcher(CancellationToken _) => Task.FromResult(Interlocked.Increment(ref version));
        await client.FetchAsync(key, Fetcher);

        var observers = Enumerable.Range(0, 300).Select(_ => client.Watch<int>(key)).ToList();
        var failures = 0;
        foreach (var observer in observers)
        {
            observer.StateChanged += state =>
            {
                if (state.HasData && state.Data <= 0)
                {
                    Interlocked.Increment(ref failures);
                }
            };
        }

        await Task.WhenAll(Enumerable.Range(0, 50).Select(i => Task.Run(() => i % 2 == 0
            ? client.InvalidateQueriesAsync(key)
            : client.RefetchQueryAsync(key))));
        await TestUtil.WaitUntilAsync(() => client.CountFetching() == 0);
        var final = client.GetQueryData<int>(key).Data;

        Assert.Equal(0, failures);
        Assert.All(observers, o => Assert.Equal(final, o.CurrentState.Data)); // every observer sees the same final state
        observers.ForEach(o => o.Dispose());
        Assert.Equal(0, client.Cache.Describe(key)!.ObserverCount);
    }

    [Fact]
    public async Task Cancelling_during_retry_back_off_stops_the_retries_and_restores_the_previous_state()
    {
        using var client = new QueryClient(TestUtil.NoRetry());
        var key = QueryKey.Create("retry-cancel");
        await client.FetchAsync(key, _ => Task.FromResult("cached"));

        for (var iteration = 0; iteration < 20; iteration++)
        {
            var attempts = 0;
            var failedOnce = TestUtil.NewSignal();
            var refetch = client.FetchAsync<string>(
                key,
                _ =>
                {
                    Interlocked.Increment(ref attempts);
                    failedOnce.TrySetResult();
                    throw new HttpRequestException("transient");
                },
                new QueryOptions { RetryCount = 5, RetryDelay = RetryPolicy.Constant(TimeSpan.FromSeconds(30)) },
                forceRefetch: true);
            await failedOnce.Task;

            await client.CancelQueriesAsync(QueryFilter.ForKey(key));
            await refetch;

            Assert.Equal(1, Volatile.Read(ref attempts)); // the 30 s back-off was abandoned, no retry ran
            var state = client.GetQueryData<string>(key);
            Assert.Equal("cached", state.Data);
            Assert.False(state.IsError);
            Assert.Equal(0, client.CountFetching());
        }
    }

    [Fact]
    public async Task Observers_disposing_themselves_and_each_other_during_notification_are_safe()
    {
        using var client = new QueryClient(TestUtil.NoRetry());
        var key = QueryKey.Create("dispose-in-handler");
        await client.FetchAsync(key, _ => Task.FromResult(0));

        for (var iteration = 0; iteration < 50; iteration++)
        {
            var observers = Enumerable.Range(0, 20).Select(_ => client.Watch<int>(key)).ToList();
            var errors = 0;
            client.DiagnosticEvent += OnEvent;
            for (var i = 0; i < observers.Count; i++)
            {
                var self = observers[i];
                var other = observers[(i + 7) % observers.Count];
                self.StateChanged += _ =>
                {
                    self.Dispose();
                    other.Dispose();
                };
            }

            await Task.WhenAll(Enumerable.Range(0, 10).Select(v => Task.Run(() => client.SetQueryData(key, v))));

            client.DiagnosticEvent -= OnEvent;
            Assert.Equal(0, errors);
            Assert.Equal(0, client.Cache.Describe(key)!.ObserverCount);
            observers.ForEach(o => o.Dispose()); // idempotent

            void OnEvent(DotNetQueryEvent e)
            {
                if (e is QueryObserverErrorEvent)
                {
                    Interlocked.Increment(ref errors);
                }
            }
        }
    }

    [Fact]
    public async Task Disposing_the_client_while_fetches_and_notifications_are_running_does_not_throw()
    {
        for (var iteration = 0; iteration < 30; iteration++)
        {
            var client = new QueryClient(TestUtil.NoRetry());
            var observers = Enumerable.Range(0, 10).Select(i => client.Watch<int>(QueryKey.Create("shutdown", i))).ToList();
            var fetches = Enumerable.Range(0, 10).Select(i => client.FetchAsync(QueryKey.Create("shutdown", i), async ct =>
            {
                await Task.Delay(1, ct);
                return i;
            })).ToList();

            var dispose = Task.Run(client.Dispose);
            await dispose;
            await Task.WhenAll(fetches.Select(f => f.ContinueWith(_ => { }, TaskScheduler.Default)));

            observers.ForEach(o => o.Dispose()); // after the client is gone: still safe
        }
    }

    [Fact]
    public async Task Evicting_inactive_queries_while_they_fetch_never_corrupts_the_cache()
    {
        using var client = new QueryClient(TestUtil.NoRetry(o => o.MaxInactiveQueries = 5));
        var running = new List<Task>();
        for (var i = 0; i < 200; i++)
        {
            var id = i;
            running.Add(Task.Run(() => client.FetchAsync(QueryKey.Create("evict", id), async ct =>
            {
                await Task.Yield();
                return id;
            })));
            if (i % 10 == 0)
            {
                running.Add(Task.Run(() => client.Cache.Trim(3)));
                running.Add(Task.Run(() => client.Cache.CollectGarbage()));
            }
        }

        await Task.WhenAll(running);
        await TestUtil.WaitUntilAsync(() => client.CountFetching() == 0);
        client.Cache.Trim(3);

        Assert.True(client.Cache.FindAll(QueryFilter.ForKey(QueryKey.Create("evict"))).Count <= 5);
        var again = await client.FetchAsync(QueryKey.Create("evict", 199), _ => Task.FromResult(-1)); // evicted entries are recreated cleanly
        Assert.True(again.Data is 199 or -1);
    }

    [Fact]
    public async Task Hydration_racing_with_fetches_never_replaces_newer_data_with_older_data()
    {
        using var client = new QueryClient(TestUtil.NoRetry());
        var key = QueryKey.Create("hydrate-race");
        var old = DateTimeOffset.UtcNow.AddMinutes(-10);

        for (var iteration = 0; iteration < 50; iteration++)
        {
            client.RemoveQuery(key);
            var fetch = Task.Run(() => client.FetchAsync(key, _ => Task.FromResult("fresh")));
            var hydrate = Task.Run(() => client.HydrateQueryData(key, "stale snapshot", old));
            await Task.WhenAll(fetch, hydrate);

            // Whatever order they ran in, the fetched (newer) value must win once both completed.
            Assert.Equal("fresh", client.GetQueryData<string>(key).Data);
        }
    }

    [Fact]
    public async Task Connectivity_flapping_during_online_mutations_runs_each_mutation_exactly_once()
    {
        var connectivity = new ConnectivityProvider();
        using var client = new QueryClient(TestUtil.NoRetry(), connectivity: connectivity);
        var executions = new int[40];
        var mutations = Enumerable.Range(0, executions.Length).Select(i => client.CreateMutation<int, int>(
            (value, _) =>
            {
                Interlocked.Increment(ref executions[value]);
                return Task.FromResult(value);
            },
            new MutationOptions<int, int> { NetworkMode = NetworkMode.Online })).ToList();

        using var flapping = new CancellationTokenSource();
        var flapper = Task.Run(async () =>
        {
            var online = false;
            while (!flapping.IsCancellationRequested)
            {
                connectivity.SetOnline(online = !online);
                await Task.Delay(1);
            }
        });

        var runs = mutations.Select((m, i) => Task.Run(() => m.MutateAsync(i))).ToList();
        await Task.Delay(100);
        await flapping.CancelAsync();
        await flapper;
        connectivity.SetOnline(true);
        var results = await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.All(executions, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task Shutting_down_during_auto_persist_leaves_a_readable_store()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dnq-shutdown-" + Guid.NewGuid().ToString("N"));
        try
        {
            for (var iteration = 0; iteration < 10; iteration++)
            {
                var client = new QueryClient(TestUtil.NoRetry());
                var persister = new FileSystemQueryPersister(directory);
                var manager = new QueryPersistenceManager(client, persister).RegisterPrefix<int>(QueryKey.Create("persist"));
                var autoPersist = manager.StartAutoPersist(TimeSpan.Zero);

                var writes = Task.Run(() =>
                {
                    for (var i = 0; i < 200; i++)
                    {
                        client.SetQueryData(QueryKey.Create("persist", i % 10), i);
                    }
                });
                await Task.Delay(5);
                autoPersist.Dispose();
                manager.Dispose();
                client.Dispose();
                await writes.ContinueWith(_ => { }, TaskScheduler.Default);

                // Every file left behind is complete: a fresh client restores without errors.
                using var restored = new QueryClient(TestUtil.NoRetry());
                using var reader = new QueryPersistenceManager(restored, new FileSystemQueryPersister(directory)).RegisterPrefix<int>(QueryKey.Create("persist"));
                var count = await reader.RestoreAllAsync();
                Assert.InRange(count, 0, 10);
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
