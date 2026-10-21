using System.Text.Json.Serialization;
using DotNetQuery;
using DotNetQuery.Persistence;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static DotNetQuery.Tests.TestUtil;

namespace DotNetQuery.Tests;

public sealed record PersistedTodo(int Id, string Title);

[JsonSerializable(typeof(List<PersistedTodo>))]
[JsonSerializable(typeof(PersistedTodo))]
internal sealed partial class PersistenceTestJsonContext : JsonSerializerContext;

public class PersistenceBehaviorTests
{
    private static readonly QueryKey TodosKey = QueryKey.Create("todos");

    [Fact]
    public async Task Restore_keeps_the_original_timestamp_so_old_data_is_refetched()
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryQueryPersister();
        using (var first = new QueryClient(NoRetry(), time))
        {
            first.SetQueryData(TodosKey, new List<PersistedTodo> { new(1, "a") });
            await new QueryPersistenceManager(first, store).Register(TodosKey, PersistenceTestJsonContext.Default.ListPersistedTodo).PersistAllAsync();
        }

        time.Advance(TimeSpan.FromHours(1));
        using var second = new QueryClient(NoRetry(o => o.DefaultStaleTime = TimeSpan.FromMinutes(5)), time);
        var manager = new QueryPersistenceManager(second, store).Register(TodosKey, PersistenceTestJsonContext.Default.ListPersistedTodo);

        Assert.Equal(1, await manager.RestoreAllAsync());
        var restored = second.GetQueryData<List<PersistedTodo>>(TodosKey);
        Assert.Equal(time.GetUtcNow().AddHours(-1), restored.DataUpdatedAt);

        var calls = 0;
        var fetched = await second.FetchAsync(TodosKey, _ => { calls++; return Task.FromResult(new List<PersistedTodo>()); });
        Assert.Equal(1, calls); // the restored data was an hour old: stale, so refetched
        Assert.Empty(fetched.Data!);
    }

    [Fact]
    public async Task MaxAge_discards_and_deletes_expired_entries()
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryQueryPersister();
        var options = new QueryPersistenceOptions { MaxAge = TimeSpan.FromDays(1), TimeProvider = time };
        using (var first = new QueryClient(NoRetry(), time))
        {
            first.SetQueryData(TodosKey, new List<PersistedTodo> { new(1, "a") });
            await new QueryPersistenceManager(first, store, options).Register(TodosKey, PersistenceTestJsonContext.Default.ListPersistedTodo).PersistAllAsync();
        }

        time.Advance(TimeSpan.FromDays(2));
        using var second = new QueryClient(NoRetry(), time);
        var manager = new QueryPersistenceManager(second, store, options).Register(TodosKey, PersistenceTestJsonContext.Default.ListPersistedTodo);

        Assert.Equal(0, await manager.RestoreAllAsync());
        Assert.False(second.GetQueryData<List<PersistedTodo>>(TodosKey).HasData);
        Assert.Empty(await store.LoadAllAsync());
    }

    [Fact]
    public async Task Buster_isolates_incompatible_versions()
    {
        var store = new InMemoryQueryPersister();
        using var v1 = new QueryClient(NoRetry(), new FakeTimeProvider());
        v1.SetQueryData(TodosKey, new List<PersistedTodo> { new(1, "a") });
        await new QueryPersistenceManager(v1, store, new QueryPersistenceOptions { Buster = "v1" })
            .Register(TodosKey, PersistenceTestJsonContext.Default.ListPersistedTodo).PersistAllAsync();

        using var v2 = new QueryClient(NoRetry(), new FakeTimeProvider());
        var manager = new QueryPersistenceManager(v2, store, new QueryPersistenceOptions { Buster = "v2" })
            .Register(TodosKey, PersistenceTestJsonContext.Default.ListPersistedTodo);

        Assert.Equal(0, await manager.RestoreAllAsync());
    }

    [Fact]
    public async Task Corrupt_payload_is_skipped_reported_and_removed()
    {
        var store = new InMemoryQueryPersister();
        await store.SaveAsync(new PersistedQuery(TodosKey.ToString(), "{ not json", DateTimeOffset.UtcNow));
        using var client = new QueryClient(NoRetry(), new FakeTimeProvider());
        var manager = new QueryPersistenceManager(client, store).Register(TodosKey, PersistenceTestJsonContext.Default.ListPersistedTodo);
        var errors = new List<QueryKey>();
        manager.Error += (key, _) => errors.Add(key);

        Assert.Equal(0, await manager.RestoreAllAsync());
        Assert.Equal([TodosKey], errors);
        Assert.Null(await store.LoadAsync(TodosKey.ToString()));
    }

    [Fact]
    public async Task Auto_persist_saves_data_changes_after_the_delay()
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryQueryPersister();
        using var client = new QueryClient(NoRetry(), time);
        using var manager = new QueryPersistenceManager(client, store, new QueryPersistenceOptions { TimeProvider = time })
            .Register(TodosKey, PersistenceTestJsonContext.Default.ListPersistedTodo);
        manager.StartAutoPersist(TimeSpan.FromSeconds(2));

        client.SetQueryData(TodosKey, new List<PersistedTodo> { new(1, "a") });
        client.SetQueryData(QueryKey.Create("unregistered"), 1);
        Assert.Empty(await store.LoadAllAsync());

        time.Advance(TimeSpan.FromSeconds(3));
        await WaitUntilAsync(() => store.LoadAllAsync().Result.Count == 1);
        Assert.Contains("\"a\"", (await store.LoadAsync(TodosKey.ToString()))!.Payload);
    }

    [Fact]
    public async Task Disposing_the_manager_stops_auto_persist()
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryQueryPersister();
        using var client = new QueryClient(NoRetry(), time);
        var manager = new QueryPersistenceManager(client, store, new QueryPersistenceOptions { TimeProvider = time })
            .Register(TodosKey, PersistenceTestJsonContext.Default.ListPersistedTodo);
        manager.StartAutoPersist();
        manager.Dispose();

        client.SetQueryData(TodosKey, new List<PersistedTodo> { new(1, "a") });
        time.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(50);

        Assert.Empty(await store.LoadAllAsync());
    }

    [Fact]
    public async Task Restore_does_not_overwrite_newer_in_memory_data()
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryQueryPersister();
        await store.SaveAsync(new PersistedQuery(TodosKey.ToString(), "[{\"id\":1,\"title\":\"old\"}]", time.GetUtcNow().AddMinutes(-5)));
        using var client = new QueryClient(NoRetry(), time);
        client.SetQueryData(TodosKey, new List<PersistedTodo> { new(1, "new") });

        await new QueryPersistenceManager(client, store).Register(TodosKey, PersistenceTestJsonContext.Default.ListPersistedTodo).RestoreAllAsync();

        Assert.Equal("new", client.GetQueryData<List<PersistedTodo>>(TodosKey).Data![0].Title);
    }

    [Fact]
    public async Task FileSystem_persister_skips_corrupt_files_and_writes_atomically()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dnq-fs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var persister = new FileSystemQueryPersister(dir);
            await persister.SaveAsync(new PersistedQuery("good", "1", DateTimeOffset.UtcNow));
            await File.WriteAllTextAsync(Path.Combine(dir, "deadbeefdeadbeef.dnq.json"), "{ truncated");

            var all = await persister.LoadAllAsync();

            Assert.Single(all);
            Assert.Equal("good", all[0].Key);
            Assert.Empty(Directory.EnumerateFiles(dir, "*.tmp"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Persistent_queue_survives_a_restart_and_replays_in_order()
    {
        var store = new InMemoryQueryPersister();
        var connectivity = new ConnectivityProvider();
        connectivity.SetOnline(false);
        var applied = new List<string>();

        using (var session1 = new PersistentMutationQueue(store, connectivity))
        {
            session1.RegisterHandler<PersistedTodo>("todos/add", (t, _) => { applied.Add(t.Title); return Task.CompletedTask; }, PersistenceTestJsonContext.Default.PersistedTodo);
            await session1.EnqueueAsync("todos/add", new PersistedTodo(1, "milk"));
            await session1.EnqueueAsync("todos/add", new PersistedTodo(2, "eggs"));
            Assert.Equal(2, session1.PendingCount);
        }

        Assert.Empty(applied);

        // The app restarts (still offline), then comes back online.
        using var session2 = new PersistentMutationQueue(store, connectivity);
        session2.RegisterHandler<PersistedTodo>("todos/add", (t, _) => { lock (applied) { applied.Add(t.Title); } return Task.CompletedTask; }, PersistenceTestJsonContext.Default.PersistedTodo);
        Assert.Equal(2, await session2.RestoreAsync());

        connectivity.SetOnline(true);
        await WaitUntilAsync(() => session2.PendingCount == 0);

        Assert.Equal(["milk", "eggs"], applied);
        Assert.Empty(await store.LoadAllAsync());
    }

    [Fact]
    public async Task Persistent_queue_drops_a_mutation_that_fails_online_and_reports_it()
    {
        var store = new InMemoryQueryPersister();
        var connectivity = new ConnectivityProvider();
        using var queue = new PersistentMutationQueue(store, connectivity);
        var failures = new List<PendingMutation>();
        queue.MutationFailed += (m, _) => failures.Add(m);
        queue.RegisterHandler<PersistedTodo>("todos/add", (_, _) => throw new InvalidOperationException("409"), PersistenceTestJsonContext.Default.PersistedTodo);

        await queue.EnqueueAsync("todos/add", new PersistedTodo(1, "dup"));

        Assert.Equal(0, queue.PendingCount);
        Assert.Single(failures);
        Assert.Equal("todos/add", failures[0].MutationKey);
    }

    [Fact]
    public async Task Persistent_queue_rejects_unregistered_or_mistyped_mutations()
    {
        using var queue = new PersistentMutationQueue(new InMemoryQueryPersister(), new ConnectivityProvider());
        queue.RegisterHandler<PersistedTodo>("todos/add", (_, _) => Task.CompletedTask, PersistenceTestJsonContext.Default.PersistedTodo);

        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.EnqueueAsync("missing", 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.EnqueueAsync("todos/add", "wrong type"));
    }

    [Fact]
    public async Task In_memory_queue_keeps_order_when_enqueuing_during_a_flush()
    {
        var connectivity = new ConnectivityProvider();
        connectivity.SetOnline(false);
        using var queue = new OfflineMutationQueue(connectivity);
        var order = new List<int>();
        var release = NewSignal();

        await queue.EnqueueAsync(async _ => { await release.Task; lock (order) { order.Add(1); } });
        connectivity.SetOnline(true); // flush starts and blocks on the first mutation
        await queue.EnqueueAsync(_ => { lock (order) { order.Add(2); } return Task.CompletedTask; }); // must queue behind #1
        release.SetResult();

        await WaitUntilAsync(() => queue.PendingCount == 0);
        await WaitUntilAsync(() => { lock (order) { return order.Count == 2; } });
        Assert.Equal([1, 2], order);
    }

    [Fact]
    public async Task In_memory_queue_keeps_a_mutation_that_failed_because_the_network_dropped()
    {
        var connectivity = new ConnectivityProvider();
        connectivity.SetOnline(false);
        using var queue = new OfflineMutationQueue(connectivity);
        var attempts = 0;

        await queue.EnqueueAsync(_ =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                connectivity.SetOnline(false);
                throw new HttpRequestException("offline again");
            }

            return Task.CompletedTask;
        });

        connectivity.SetOnline(true);
        await WaitUntilAsync(() => Volatile.Read(ref attempts) == 1);
        await Task.Delay(20);
        Assert.Equal(1, queue.PendingCount);

        connectivity.SetOnline(true);
        await WaitUntilAsync(() => queue.PendingCount == 0);
        Assert.Equal(2, attempts);
    }
}
