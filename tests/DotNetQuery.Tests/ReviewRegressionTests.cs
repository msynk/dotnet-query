using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using DotNetQuery;
using DotNetQuery.Persistence;
using DotNetQuery.Synchronization;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Tests;

/// <summary>Regression tests for the issues found by the independent review of the improvement work.</summary>
public class ReviewRegressionTests
{
    private sealed record NewTodo(string Title);

    private static readonly JsonTypeInfo<NewTodo> TodoInfo = (JsonTypeInfo<NewTodo>)TestUtil.WebJson.GetTypeInfo(typeof(NewTodo));
    private static readonly JsonTypeInfo<List<string>> ListInfo = (JsonTypeInfo<List<string>>)TestUtil.WebJson.GetTypeInfo(typeof(List<string>));

    /// <summary>A store whose LoadAllAsync can be held open, and whose saves can be made to fail.</summary>
    private sealed class ControlledStore : IQueryPersister
    {
        private readonly InMemoryQueryPersister _inner = new();

        public TaskCompletionSource? HoldLoad { get; set; }

        public bool FailSaves { get; set; }

        public Task SaveAsync(PersistedQuery query, CancellationToken cancellationToken = default) =>
            FailSaves ? throw new IOException("disk full") : _inner.SaveAsync(query, cancellationToken);

        public Task<PersistedQuery?> LoadAsync(string key, CancellationToken cancellationToken = default) => _inner.LoadAsync(key, cancellationToken);

        public async Task<IReadOnlyList<PersistedQuery>> LoadAllAsync(CancellationToken cancellationToken = default)
        {
            var snapshot = await _inner.LoadAllAsync(cancellationToken);
            if (HoldLoad is { } hold)
            {
                await hold.Task;
            }

            return snapshot;
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default) => _inner.RemoveAsync(key, cancellationToken);

        public Task ClearAsync(CancellationToken cancellationToken = default) => _inner.ClearAsync(cancellationToken);
    }

    [Fact]
    public async Task Clearing_the_queue_during_a_restore_never_resurrects_the_cleared_mutations()
    {
        var store = new ControlledStore();
        var connectivity = new ConnectivityProvider();
        connectivity.SetOnline(false);
        using (var previous = new PersistentMutationQueue(store, connectivity).RegisterHandler<NewTodo>("add", (_, _) => Task.CompletedTask, TodoInfo))
        {
            await previous.EnqueueAsync("add", new NewTodo("previous user's write"));
        }

        var replayed = 0;
        using var queue = new PersistentMutationQueue(store, connectivity).RegisterHandler<NewTodo>("add", (_, _) => { replayed++; return Task.CompletedTask; }, TodoInfo);
        store.HoldLoad = TestUtil.NewSignal();
        var restore = queue.RestoreAsync();
        var clear = queue.ClearAsync();
        store.HoldLoad.SetResult();
        store.HoldLoad = null;
        await Task.WhenAll(restore, clear);

        connectivity.SetOnline(true);
        await queue.FlushAsync();

        Assert.Equal(0, queue.PendingCount);
        Assert.Equal(0, replayed);
        Assert.Empty(await store.LoadAllAsync());
    }

    [Fact]
    public async Task Clearing_before_restoring_also_drops_entries_still_in_the_store()
    {
        var store = new ControlledStore();
        var connectivity = new ConnectivityProvider();
        connectivity.SetOnline(false);
        using (var previous = new PersistentMutationQueue(store, connectivity).RegisterHandler<NewTodo>("add", (_, _) => Task.CompletedTask, TodoInfo))
        {
            await previous.EnqueueAsync("add", new NewTodo("x"));
        }

        using var queue = new PersistentMutationQueue(store, connectivity).RegisterHandler<NewTodo>("add", (_, _) => Task.CompletedTask, TodoInfo);
        await queue.ClearAsync();

        Assert.Equal(0, await queue.RestoreAsync());
    }

    [Fact]
    public async Task Retrying_an_expired_dead_letter_restarts_its_age_and_a_failed_save_keeps_it()
    {
        var time = new FakeTimeProvider();
        var store = new ControlledStore();
        var connectivity = new ConnectivityProvider();
        connectivity.SetOnline(false);
        var replayed = 0;
        using var queue = new PersistentMutationQueue(store, connectivity, new PersistentMutationQueueOptions
        {
            TimeProvider = time,
            MaxAge = TimeSpan.FromHours(1),
            UseDeadLetterQueue = true,
        }).RegisterHandler<NewTodo>("add", (_, _) => { replayed++; return Task.CompletedTask; }, TodoInfo);
        await queue.EnqueueAsync("add", new NewTodo("late"));
        time.Advance(TimeSpan.FromHours(2));
        connectivity.SetOnline(true);
        await queue.FlushAsync();
        var id = Assert.Single(queue.DeadLetters).Mutation.Id;

        store.FailSaves = true;
        await Assert.ThrowsAsync<IOException>(() => queue.RetryDeadLetterAsync(id));
        Assert.Single(queue.DeadLetters);

        store.FailSaves = false;
        Assert.True(await queue.RetryDeadLetterAsync(id));
        Assert.Equal(1, replayed);
        Assert.Empty(queue.DeadLetters);
    }

    [Fact]
    public async Task A_throwing_retry_delay_fails_the_mutation_instead_of_escaping_the_flush()
    {
        var connectivity = new ConnectivityProvider();
        var failed = 0;
        using var queue = new PersistentMutationQueue(new InMemoryQueryPersister(), connectivity, new PersistentMutationQueueOptions
        {
            MaxAttempts = 3,
            RetryDelay = (_, _) => throw new InvalidOperationException("bad strategy"),
        }).RegisterHandler<NewTodo>("add", (_, _) => throw new TimeoutException(), TodoInfo);
        queue.MutationFailed += (_, _) => failed++;

        await queue.EnqueueAsync("add", new NewTodo("x"));

        Assert.Equal(1, failed);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public void Local_writes_made_while_a_remote_change_is_applied_are_still_broadcast()
    {
        var hub = new InMemoryQuerySyncHub();
        var a = QueryKey.Create("a");
        var c = QueryKey.Create("c");
        using var tabA = new QueryClient(TestUtil.NoRetry());
        using var tabB = new QueryClient(TestUtil.NoRetry());
        using var syncA = new QuerySynchronizer(tabA, hub.CreateChannel()).Register(a, ListInfo).Register(c, ListInfo).Start();
        using var syncB = new QuerySynchronizer(tabB, hub.CreateChannel()).Register(a, ListInfo).Register(c, ListInfo).Start();

        // In tab B, reacting to A's data writes C - on the thread that is applying the remote message.
        using var observer = tabB.Watch<List<string>>(a);
        observer.StateChanged += state =>
        {
            if (state.HasData)
            {
                tabB.SetQueryData(c, new List<string> { "derived in B" });
            }
        };

        tabA.SetQueryData(a, new List<string> { "from A" });

        Assert.Equal(["derived in B"], tabA.GetQueryData<List<string>>(c).Data);
    }

    [Fact]
    public async Task Filtered_transactions_never_run_updaters_on_queries_that_lost_their_data()
    {
        using var client = new QueryClient(TestUtil.NoRetry());
        var key = QueryKey.Create("lists", 1);
        using var observer = client.Watch<List<string>>(key); // observed: removal resets it in place
        client.SetQueryData(key, new List<string> { "x" });
        var errors = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var writer = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    client.SetQueriesData<List<string>>(QueryFilter.ForKey(QueryKey.Create("lists")), list => [.. list!, "y"]);
                }
                catch (NullReferenceException)
                {
                    Interlocked.Increment(ref errors);
                }
            }
        });
        var remover = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                client.RemoveQuery(key);
                client.SetQueryData(key, new List<string> { "x" });
            }
        });
        await Task.WhenAll(writer, remover);

        Assert.Equal(0, errors);
    }

    private readonly struct BrokenEquals(int value) : IEquatable<BrokenEquals>
    {
        public int Value { get; } = value;

        public bool Equals(BrokenEquals other) => throw new InvalidOperationException("broken");

        public override bool Equals(object? obj) => throw new InvalidOperationException("broken");

        public override int GetHashCode() => Value;
    }

    [Fact]
    public async Task A_throwing_value_type_Equals_never_stops_an_entrys_notifications()
    {
        using var client = new QueryClient(TestUtil.NoRetry());
        var key = QueryKey.Create("struct");
        using var selected = client.Watch(key, (BrokenEquals b) => b.Value);
        using var plain = client.Watch<BrokenEquals>(key);
        var notifications = 0;
        plain.StateChanged += _ => Interlocked.Increment(ref notifications);

        client.SetQueryData(key, new BrokenEquals(1));
        client.SetQueryData(key, new BrokenEquals(2));
        client.SetQueryData(key, new BrokenEquals(3));

        await TestUtil.WaitUntilAsync(() => Volatile.Read(ref notifications) == 3);
        Assert.Equal(3, selected.CurrentState.Data);
    }

    private sealed record Todo(int Id, string Title);

    [Fact]
    public async Task A_prefixed_record_waits_while_its_query_exists_with_another_type()
    {
        var info = (JsonTypeInfo<Todo>)TestUtil.WebJson.GetTypeInfo(typeof(Todo));
        var store = new InMemoryQueryPersister();
        await store.SaveAsync(new PersistedQuery("[\"todo\", 1]", JsonSerializer.Serialize(new Todo(1, "saved"), info), DateTimeOffset.UtcNow));
        using var client = new QueryClient(TestUtil.NoRetry(o => o.DefaultStaleTime = TimeSpan.FromMinutes(5)));
        using var manager = new QueryPersistenceManager(client, store).RegisterPrefix(QueryKey.Create("todo"), info);
        await manager.RestoreAllAsync();

        client.SetQueryData(QueryKey.Create("todo", 1), 42); // same key, different type: not applicable
        client.RemoveQuery(QueryKey.Create("todo", 1));

        var restored = await client.FetchAsync(QueryKey.Create("todo", 1), _ => Task.FromResult(new Todo(1, "server")));
        Assert.Equal("saved", restored.Data!.Title);
    }

    [Fact]
    public async Task Waiters_of_a_fetch_reset_by_removing_an_observed_query_receive_the_refetch()
    {
        using var client = new QueryClient(TestUtil.NoRetry());
        var key = QueryKey.Create("observed-reset");
        using var observer = client.Watch<int>(key);
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

            return 5;
        }

        var waiting = client.FetchAsync(key, Fetcher);
        await started.Task;
        client.RemoveQuery(key);

        Assert.Equal(5, (await waiting).Data);
    }
}
