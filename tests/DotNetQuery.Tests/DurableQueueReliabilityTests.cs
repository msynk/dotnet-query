using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using DotNetQuery;
using DotNetQuery.Persistence;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Tests;

public class DurableQueueReliabilityTests
{
    private sealed record NewTodo(string Title);

    private static readonly JsonTypeInfo<NewTodo> TodoInfo = (JsonTypeInfo<NewTodo>)TestUtil.WebJson.GetTypeInfo(typeof(NewTodo));

    private readonly InMemoryQueryPersister _store = new();
    private readonly ConnectivityProvider _connectivity = new();
    private readonly FakeTimeProvider _time = new();

    private PersistentMutationQueue Queue(PersistentMutationQueueOptions? options = null) =>
        new(_store, _connectivity, (options ?? new PersistentMutationQueueOptions()) with { TimeProvider = _time });

    [Fact]
    public async Task Transient_failures_are_retried_and_permanent_ones_are_not()
    {
        using var queue = Queue(new PersistentMutationQueueOptions { MaxAttempts = 3, ShouldRetry = HttpRetryPolicy.ShouldRetry });
        var attempts = new Dictionary<string, int>();
        queue.RegisterHandler<NewTodo>("add", (todo, _) =>
        {
            attempts[todo.Title] = attempts.GetValueOrDefault(todo.Title) + 1;
            return todo.Title switch
            {
                "flaky" when attempts[todo.Title] < 3 => throw new HttpRequestException("busy", null, System.Net.HttpStatusCode.ServiceUnavailable),
                "invalid" => throw new HttpRequestException("bad", null, System.Net.HttpStatusCode.BadRequest),
                _ => Task.CompletedTask,
            };
        }, TodoInfo);
        var failed = new List<string>();
        queue.MutationFailed += (m, _) => failed.Add(m.MutationKey);

        await queue.EnqueueAsync("add", new NewTodo("flaky"));
        await queue.EnqueueAsync("add", new NewTodo("invalid"));

        Assert.Equal(3, attempts["flaky"]);
        Assert.Equal(1, attempts["invalid"]);
        Assert.Equal(["add"], failed);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task Permanent_failures_go_to_a_durable_dead_letter_queue()
    {
        var reject = true;
        Task Handler(NewTodo todo, CancellationToken _) => reject ? throw new InvalidOperationException("rejected") : Task.CompletedTask;
        string id;
        using (var queue = Queue(new PersistentMutationQueueOptions { UseDeadLetterQueue = true }).RegisterHandler<NewTodo>("add", Handler, TodoInfo))
        {
            await queue.EnqueueAsync("add", new NewTodo("milk"));

            var dead = Assert.Single(queue.DeadLetters);
            Assert.Equal("rejected", dead.Error);
            Assert.Equal(0, queue.PendingCount);
            id = dead.Mutation.Id;
        }

        // Survives a restart, and can be retried once the cause is fixed.
        using var restarted = Queue(new PersistentMutationQueueOptions { UseDeadLetterQueue = true }).RegisterHandler<NewTodo>("add", Handler, TodoInfo);
        await restarted.RestoreAsync();
        Assert.Equal(id, Assert.Single(restarted.DeadLetters).Mutation.Id);

        reject = false;
        Assert.True(await restarted.RetryDeadLetterAsync(id));

        Assert.Empty(restarted.DeadLetters);
        Assert.Equal(0, restarted.PendingCount);
        Assert.Empty(await _store.LoadAllAsync());
    }

    [Fact]
    public async Task Dead_letters_can_be_discarded()
    {
        using var queue = Queue(new PersistentMutationQueueOptions { UseDeadLetterQueue = true })
            .RegisterHandler<NewTodo>("add", (_, _) => throw new InvalidOperationException("no"), TodoInfo);
        await queue.EnqueueAsync("add", new NewTodo("x"));

        Assert.True(await queue.DiscardDeadLetterAsync(queue.DeadLetters[0].Mutation.Id));

        Assert.Empty(queue.DeadLetters);
        Assert.Empty(await _store.LoadAllAsync());
        Assert.False(await queue.DiscardDeadLetterAsync("missing"));
    }

    [Fact]
    public async Task Expired_mutations_are_not_replayed()
    {
        _connectivity.SetOnline(false);
        var replayed = 0;
        using var queue = Queue(new PersistentMutationQueueOptions { MaxAge = TimeSpan.FromHours(1), UseDeadLetterQueue = true })
            .RegisterHandler<NewTodo>("add", (_, _) => { replayed++; return Task.CompletedTask; }, TodoInfo);
        await queue.EnqueueAsync("add", new NewTodo("old"));
        _time.Advance(TimeSpan.FromHours(2));
        await queue.EnqueueAsync("add", new NewTodo("new"));

        _connectivity.SetOnline(true);
        await TestUtil.WaitUntilAsync(() => queue.PendingCount == 0);

        Assert.Equal(1, replayed);
        Assert.Contains("expired", Assert.Single(queue.DeadLetters).Error);
    }

    [Fact]
    public async Task Handlers_receive_a_stable_idempotency_key_across_restarts()
    {
        _connectivity.SetOnline(false);
        using (var offline = Queue().RegisterHandler<NewTodo>("add", (_, _, _) => Task.CompletedTask, TodoInfo))
        {
            await offline.EnqueueAsync("add", new NewTodo("milk"));
        }

        var expectedId = (await _store.LoadAllAsync()).Single().Key["dnq-mutation:".Length..];
        string? receivedId = null;
        using var restarted = Queue().RegisterHandler<NewTodo>("add", (_, mutation, _) => { receivedId = mutation.Id; return Task.CompletedTask; }, TodoInfo);
        await restarted.RestoreAsync();

        _connectivity.SetOnline(true);
        await TestUtil.WaitUntilAsync(() => receivedId is not null);
        Assert.Equal(expectedId, receivedId);
    }

    [Fact]
    public async Task Paused_queues_store_but_do_not_replay_until_resumed()
    {
        var replayed = 0;
        using var queue = Queue().RegisterHandler<NewTodo>("add", (_, _) => { replayed++; return Task.CompletedTask; }, TodoInfo);
        queue.Pause();

        await queue.EnqueueAsync("add", new NewTodo("a"));
        await queue.EnqueueAsync("add", new NewTodo("b"));
        Assert.True(queue.IsPaused);
        Assert.Equal(0, replayed);
        Assert.Equal(2, queue.PendingCount);

        await queue.ResumeAsync();

        Assert.Equal(2, replayed);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task Queued_mutations_can_be_removed_or_cleared()
    {
        _connectivity.SetOnline(false);
        using var queue = Queue().RegisterHandler<NewTodo>("add", (_, _) => Task.CompletedTask, TodoInfo);
        await queue.EnqueueAsync("add", new NewTodo("a"));
        await queue.EnqueueAsync("add", new NewTodo("b"));
        await queue.EnqueueAsync("add", new NewTodo("c"));

        Assert.True(await queue.RemoveAsync(queue.Pending[1].Id));
        Assert.Equal(2, queue.PendingCount);
        Assert.False(await queue.RemoveAsync("missing"));

        await queue.ClearAsync();

        Assert.Equal(0, queue.PendingCount);
        Assert.Empty(await _store.LoadAllAsync());
    }
}
