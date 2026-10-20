using DotNetQuery;
using DotNetQuery.Persistence;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static DotNetQuery.Core.Tests.TestUtil;

namespace DotNetQuery.Core.Tests;

/// <summary>Regression tests for interleavings found in review of the fetch/mutation engine.</summary>
public class ConcurrencyRegressionTests
{
    [Fact]
    public async Task A_new_fetch_does_not_join_a_fetch_that_is_being_cancelled()
    {
        using var client = new QueryClient(NoRetry(o => o.DefaultCancelInFlightWhenUnobserved = true), new FakeTimeProvider());
        var key = QueryKey.Create("rejoin");
        var started = NewSignal();
        var release = NewSignal<int>();
        var calls = 0;

        Task<int> Fetcher(CancellationToken _)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.TrySetResult();
                return release.Task; // ignores its token
            }

            return Task.FromResult(2);
        }

        var observer = client.Watch<int>(key);
        _ = client.FetchAsync(key, Fetcher);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        observer.Dispose(); // cancels the in-flight fetch (opt-in policy)

        var remount = client.FetchAsync(key, Fetcher); // e.g. the component is shown again
        release.SetResult(1);

        var result = await remount.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Data);
    }

    [Fact]
    public async Task Invalidating_while_offline_drops_the_outdated_in_flight_result()
    {
        var connectivity = new ConnectivityProvider();
        using var client = new QueryClient(NoRetry(), new FakeTimeProvider(), connectivity: connectivity);
        var key = QueryKey.Create("offline-supersede");
        var started = NewSignal();
        var release = NewSignal<string>();
        var calls = 0;

        Task<string> Fetcher(CancellationToken _)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.TrySetResult();
                return release.Task;
            }

            return Task.FromResult("fresh");
        }

        using var observer = client.Watch<string>(key);
        var first = client.FetchAsync(key, Fetcher);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        connectivity.SetOnline(false);
        await client.InvalidateQueriesAsync(key);
        release.SetResult("outdated");
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual("outdated", client.GetQueryData<string>(key).Data);

        connectivity.SetOnline(true);
        await WaitUntilAsync(() => client.GetQueryData<string>(key).Data == "fresh");
    }

    [Fact]
    public async Task SetQueryData_updater_does_not_run_under_the_entry_lock()
    {
        using var client = new QueryClient(NoRetry(), new FakeTimeProvider());
        var key = QueryKey.Create("reentrant");
        client.SetQueryData(key, 1);

        var update = Task.Run(() => client.SetQueryData<int>(key, current =>
        {
            // Reading the same entry from another thread inside the updater must not deadlock.
            var seen = Task.Run(() => client.GetQueryData<int>(key).Data).Wait(TimeSpan.FromSeconds(5));
            Assert.True(seen);
            return current + 1;
        }));

        await update.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, client.GetQueryData<int>(key).Data);
    }

    [Fact]
    public async Task Rollback_preserves_a_pending_invalidation()
    {
        var time = new FakeTimeProvider();
        using var client = new QueryClient(NoRetry(o => o.DefaultStaleTime = TimeSpan.FromMinutes(5)), time);
        var key = QueryKey.Create("stale-after-rollback");
        var calls = 0;
        Task<int> Fetcher(CancellationToken _) => Task.FromResult(Interlocked.Increment(ref calls) * 10);
        await client.FetchAsync(key, Fetcher);
        await client.InvalidateQueriesAsync(key); // unobserved: marked stale, not refetched

        await client.MutateAsync<int, int>(
            (_, _) => Task.FromException<int>(new InvalidOperationException()),
            1,
            new MutationOptions<int, int>
            {
                ThrowOnError = false,
                OptimisticUpdates = [OptimisticUpdate.Create<int, int>(key, (current, add) => current + add)],
            });

        Assert.Equal(10, client.GetQueryData<int>(key).Data);
        var next = await client.FetchAsync(key, Fetcher);
        Assert.Equal(20, next.Data); // still stale after the rollback, so it refetches
    }

    [Fact]
    public async Task Rollback_does_not_clobber_a_write_that_landed_on_top()
    {
        using var client = new QueryClient(NoRetry(), new FakeTimeProvider());
        var key = QueryKey.Create("todos");
        var server = new List<string> { "a" };
        await client.FetchAsync(key, _ => Task.FromResult(server.ToList()));
        using var observer = client.Watch<List<string>>(key);
        var started = NewSignal();
        var fail = NewSignal<string>();

        var m1 = client.MutateAsync<string, string>(
            async (title, _) => { started.TrySetResult(); return await fail.Task; },
            "x",
            new MutationOptions<string, string>
            {
                ThrowOnError = false,
                OptimisticUpdates = [OptimisticUpdate.Create<string, List<string>>(key, (todos, t) => [.. todos ?? [], t])],
            });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Another mutation commits "y" while the first is still pending.
        server.Add("y");
        client.SetQueryData<List<string>>(key, todos => [.. todos ?? [], "y"]);

        fail.SetException(new InvalidOperationException("x rejected"));
        await m1;

        await WaitUntilAsync(() => client.GetQueryData<List<string>>(key).Data!.SequenceEqual(["a", "y"]));
    }

    [Fact]
    public async Task Entries_with_a_fetch_paused_offline_are_not_garbage_collected()
    {
        var time = new FakeTimeProvider();
        var connectivity = new ConnectivityProvider();
        connectivity.SetOnline(false);
        using var client = new QueryClient(NoRetry(o => o.DefaultCacheTime = TimeSpan.FromMinutes(1)), time, connectivity: connectivity);
        var key = QueryKey.Create("deferred");
        await client.FetchAsync(key, _ => Task.FromResult(5));

        time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(0, client.Cache.CollectGarbage());

        connectivity.SetOnline(true);
        await WaitUntilAsync(() => client.GetQueryData<int>(key).Data == 5);
    }

    [Fact]
    public async Task Cancelling_the_wait_for_reconciliation_does_not_turn_a_committed_mutation_into_a_failure()
    {
        using var client = new QueryClient(NoRetry(), new FakeTimeProvider());
        var key = QueryKey.Create("slow-reconcile");
        var refetchRelease = NewSignal<int>();
        var fetches = 0;
        Task<int> Fetcher(CancellationToken _) => Interlocked.Increment(ref fetches) == 1 ? Task.FromResult(0) : refetchRelease.Task;
        using var observer = client.Watch<int>(key);
        await client.FetchAsync(key, Fetcher);
        using var cts = new CancellationTokenSource();
        var errors = 0;

        var run = client.MutateAsync<int, int>(
            (v, _) => Task.FromResult(v),
            1,
            new MutationOptions<int, int>
            {
                OptimisticUpdates = [OptimisticUpdate.Create<int, int>(key, (_, v) => v)],
                Invalidates = [key],
                OnError = (_, _, _, _) => { errors++; return ValueTask.CompletedTask; },
            },
            cts.Token);

        await WaitUntilAsync(() => Volatile.Read(ref fetches) == 2); // waiting on the reconciliation refetch
        cts.Cancel();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.IsSuccess);
        Assert.Equal(0, errors);
        Assert.Equal(1, client.GetQueryData<int>(key).Data); // not rolled back
        refetchRelease.SetResult(1);
    }

    [Fact]
    public async Task Persistent_queue_replays_in_call_order_even_when_saves_complete_out_of_order()
    {
        var store = new SlowFirstSaveStore();
        var connectivity = new ConnectivityProvider();
        connectivity.SetOnline(false);
        using var queue = new PersistentMutationQueue(store, connectivity);
        var applied = new List<string>();
        queue.RegisterHandler<PersistedTodo>("add", (t, _) => { lock (applied) { applied.Add(t.Title); } return Task.CompletedTask; }, PersistenceTestJsonContext.Default.PersistedTodo);

        var first = queue.EnqueueAsync("add", new PersistedTodo(1, "first"));
        var second = queue.EnqueueAsync("add", new PersistedTodo(2, "second"));
        await Task.WhenAll(first, second);

        connectivity.SetOnline(true);
        await WaitUntilAsync(() => queue.PendingCount == 0);
        Assert.Equal(["first", "second"], applied);
    }

    private sealed class SlowFirstSaveStore : IQueryPersister
    {
        private readonly InMemoryQueryPersister _inner = new();
        private int _saves;

        public async Task SaveAsync(PersistedQuery query, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _saves) == 1)
            {
                await Task.Delay(100, cancellationToken);
            }

            await _inner.SaveAsync(query, cancellationToken);
        }

        public Task<PersistedQuery?> LoadAsync(string key, CancellationToken cancellationToken = default) => _inner.LoadAsync(key, cancellationToken);

        public Task<IReadOnlyList<PersistedQuery>> LoadAllAsync(CancellationToken cancellationToken = default) => _inner.LoadAllAsync(cancellationToken);

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default) => _inner.RemoveAsync(key, cancellationToken);

        public Task ClearAsync(CancellationToken cancellationToken = default) => _inner.ClearAsync(cancellationToken);
    }
}
