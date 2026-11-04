using System.Collections.Concurrent;
using System.Text.Json.Serialization.Metadata;
using DotNetQuery;
using DotNetQuery.Persistence;
using Xunit;

namespace DotNetQuery.Tests;

/// <summary>Several queues sharing one store (browser tabs over one IndexedDB store, processes over one directory).</summary>
public class SharedQueueLockTests
{
    private sealed record NewTodo(string Title);

    private static readonly JsonTypeInfo<NewTodo> TodoInfo = (JsonTypeInfo<NewTodo>)TestUtil.WebJson.GetTypeInfo(typeof(NewTodo));

    private static ConnectivityProvider Offline()
    {
        var connectivity = new ConnectivityProvider();
        connectivity.SetOnline(false);
        return connectivity;
    }

    /// <summary>An in-process stand-in for the Web Locks API or a lock file.</summary>
    private sealed class SemaphoreLock : IMutationQueueLock
    {
        private readonly SemaphoreSlim _semaphore = new(1, 1);

        public int Acquisitions;

        public async ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default)
        {
            await _semaphore.WaitAsync(cancellationToken);
            Interlocked.Increment(ref Acquisitions);
            return new Release(_semaphore);
        }

        private sealed class Release(SemaphoreSlim semaphore) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                semaphore.Release();
                return ValueTask.CompletedTask;
            }
        }
    }

    [Fact]
    public async Task Two_instances_sharing_a_store_replay_each_queued_mutation_once()
    {
        var store = new InMemoryQueryPersister();
        var sharedLock = new SemaphoreLock();
        var tabA = Offline();
        var tabB = Offline();
        var sent = new ConcurrentQueue<string>();
        Task Send(NewTodo todo, CancellationToken _)
        {
            sent.Enqueue(todo.Title);
            return Task.Delay(5);
        }

        var options = new PersistentMutationQueueOptions { ReplayLock = sharedLock };
        using var queueA = new PersistentMutationQueue(store, tabA, options).RegisterHandler<NewTodo>("add", Send, TodoInfo);
        using var queueB = new PersistentMutationQueue(store, tabB, options).RegisterHandler<NewTodo>("add", Send, TodoInfo);

        // Both tabs queue writes while offline, and each restores what the other stored (a reload of the second tab).
        await queueA.EnqueueAsync("add", new NewTodo("a1"));
        await queueB.EnqueueAsync("add", new NewTodo("b1"));
        await queueA.EnqueueAsync("add", new NewTodo("a2"));
        await queueA.RestoreAsync();
        await queueB.RestoreAsync();
        Assert.Equal(3, queueA.PendingCount);
        Assert.Equal(3, queueB.PendingCount);

        // Both reconnect at once.
        var flushA = Task.Run(() => { tabA.SetOnline(true); return queueA.FlushAsync(); });
        var flushB = Task.Run(() => { tabB.SetOnline(true); return queueB.FlushAsync(); });
        await Task.WhenAll(flushA, flushB);
        await TestUtil.WaitUntilAsync(() => sent.Count >= 3);
        await queueA.FlushAsync();
        await queueB.FlushAsync();

        Assert.Equal(["a1", "b1", "a2"], sent.ToArray());
        Assert.Equal(0, queueA.PendingCount);
        Assert.Equal(0, queueB.PendingCount);
        Assert.Empty(await store.LoadAllAsync());
        Assert.True(sharedLock.Acquisitions >= 2);
    }

    [Fact]
    public async Task Without_a_lock_ids_from_two_instances_never_collide()
    {
        var store = new InMemoryQueryPersister();
        var offline = Offline();
        using var queueA = new PersistentMutationQueue(store, offline).RegisterHandler<NewTodo>("add", (_, _) => Task.CompletedTask, TodoInfo);
        using var queueB = new PersistentMutationQueue(store, offline).RegisterHandler<NewTodo>("add", (_, _) => Task.CompletedTask, TodoInfo);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => (i % 2 == 0 ? queueA : queueB).EnqueueAsync("add", new NewTodo($"t{i}"))));

        Assert.Equal(20, (await store.LoadAllAsync()).Count);
    }

    [Fact]
    public async Task Restore_drops_entries_another_instance_replayed_or_removed()
    {
        var store = new InMemoryQueryPersister();
        var offline = Offline();
        using var queueA = new PersistentMutationQueue(store, offline).RegisterHandler<NewTodo>("add", (_, _) => Task.CompletedTask, TodoInfo);
        using var queueB = new PersistentMutationQueue(store, offline).RegisterHandler<NewTodo>("add", (_, _) => Task.CompletedTask, TodoInfo);
        await queueA.EnqueueAsync("add", new NewTodo("x"));
        await queueA.EnqueueAsync("add", new NewTodo("y"));
        await queueB.RestoreAsync();
        var changes = 0;
        queueA.PendingChanged += () => changes++;

        Assert.True(await queueB.RemoveAsync(queueB.Pending[0].Id));
        await queueA.RestoreAsync();

        Assert.Equal(queueB.Pending.Select(p => p.Id), queueA.Pending.Select(p => p.Id));
        Assert.Equal(1, queueA.PendingCount);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task File_lock_admits_one_holder_at_a_time()
    {
        var path = Path.Combine(Path.GetTempPath(), "dnq-lock-" + Guid.NewGuid().ToString("N"), "queue.lock");
        var first = new FileMutationQueueLock(path, TimeSpan.FromMilliseconds(10));
        var second = new FileMutationQueueLock(path, TimeSpan.FromMilliseconds(10));
        try
        {
            var held = await first.AcquireAsync();
            var waiting = second.AcquireAsync().AsTask();
            await Task.Delay(100);
            Assert.False(waiting.IsCompleted);

            await held.DisposeAsync();
            var acquired = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            await acquired.DisposeAsync();

            using var cts = new CancellationTokenSource();
            var again = await first.AcquireAsync();
            var cancelled = second.AcquireAsync(cts.Token).AsTask();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            await again.DisposeAsync();
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
