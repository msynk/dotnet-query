using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using DotNetQuery;
using DotNetQuery.Persistence;
using Xunit;

namespace DotNetQuery.Tests;

public class MigrationReplayDiagnosticsTests
{
    private sealed record Todo(int Id, string Title);

    private sealed record NewTodo(string Title);

    private static readonly JsonTypeInfo<Todo> TodoInfo = (JsonTypeInfo<Todo>)TestUtil.WebJson.GetTypeInfo(typeof(Todo));
    private static readonly JsonTypeInfo<NewTodo> NewTodoInfo = (JsonTypeInfo<NewTodo>)TestUtil.WebJson.GetTypeInfo(typeof(NewTodo));

    [Fact]
    public async Task Data_persisted_under_an_older_buster_is_migrated_instead_of_discarded()
    {
        var store = new InMemoryQueryPersister();
        var key = QueryKey.Create("todo", 1);
        await store.SaveAsync(new PersistedQuery("v1|" + key, "{\"id\":1,\"name\":\"old shape\"}", DateTimeOffset.UtcNow));
        await store.SaveAsync(new PersistedQuery("v1|" + QueryKey.Create("todo", 2), "{\"id\":2,\"name\":\"drop me\"}", DateTimeOffset.UtcNow));
        await store.SaveAsync(new PersistedQuery(QueryKey.Create("todo", 3).ToString(), "{\"id\":3,\"title\":\"no buster\"}", DateTimeOffset.UtcNow));
        using var client = new QueryClient(TestUtil.NoRetry());
        using var manager = new QueryPersistenceManager(client, store, new QueryPersistenceOptions
        {
            Buster = "v2",
            Migrations = new Dictionary<string, Func<string, string?>>
            {
                ["v1"] = payload => payload.Contains("drop me") ? null : payload.Replace("\"name\":", "\"title\":"),
                [""] = payload => payload,
            },
        })
            .Register(key, TodoInfo)
            .Register(QueryKey.Create("todo", 2), TodoInfo)
            .Register(QueryKey.Create("todo", 3), TodoInfo);

        Assert.Equal(2, await manager.RestoreAllAsync());

        Assert.Equal("old shape", client.GetQueryData<Todo>(key).Data!.Title);
        Assert.Equal("no buster", client.GetQueryData<Todo>(QueryKey.Create("todo", 3)).Data!.Title);
        Assert.False(client.GetQueryData<Todo>(QueryKey.Create("todo", 2)).HasData);
        Assert.Equal(["v2|" + key, "v2|" + QueryKey.Create("todo", 3)], (await store.LoadAllAsync()).Select(r => r.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Replay_failure_policy_dead_letters_conflicts_and_pauses_on_401()
    {
        var connectivity = new ConnectivityProvider();
        var unauthorized = true;
        using var queue = new PersistentMutationQueue(new InMemoryQueryPersister(), connectivity, new PersistentMutationQueueOptions
        {
            UseDeadLetterQueue = true,
            OnReplayFailure = (_, error, _) =>
                HttpRetryPolicy.IsConflict(error) ? ReplayDecision.Fail
                : error is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } ? ReplayDecision.Pause
                : ReplayDecision.Default,
        }).RegisterHandler<NewTodo>("add", (todo, _) => todo.Title switch
        {
            "conflicting" => throw new HttpRequestException("taken", null, HttpStatusCode.Conflict),
            "needs-auth" when unauthorized => throw new HttpRequestException("expired", null, HttpStatusCode.Unauthorized),
            _ => Task.CompletedTask,
        }, NewTodoInfo);

        await queue.EnqueueAsync("add", new NewTodo("conflicting"));
        Assert.Single(queue.DeadLetters);

        await queue.EnqueueAsync("add", new NewTodo("needs-auth"));
        await queue.EnqueueAsync("add", new NewTodo("after"));
        Assert.True(queue.IsPaused);
        Assert.Equal(2, queue.PendingCount); // kept in order behind the paused head

        unauthorized = false;
        await queue.ResumeAsync();
        Assert.Equal(0, queue.PendingCount);
    }

    private sealed class FlakyRemoveStore : IQueryPersister
    {
        private readonly InMemoryQueryPersister _inner = new();

        public int FailRemoves { get; set; }

        public Task SaveAsync(PersistedQuery query, CancellationToken cancellationToken = default) => _inner.SaveAsync(query, cancellationToken);

        public Task<PersistedQuery?> LoadAsync(string key, CancellationToken cancellationToken = default) => _inner.LoadAsync(key, cancellationToken);

        public Task<IReadOnlyList<PersistedQuery>> LoadAllAsync(CancellationToken cancellationToken = default) => _inner.LoadAllAsync(cancellationToken);

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default) =>
            FailRemoves-- > 0 ? throw new IOException("crash before removal") : _inner.RemoveAsync(key, cancellationToken);

        public Task ClearAsync(CancellationToken cancellationToken = default) => _inner.ClearAsync(cancellationToken);
    }

    [Fact]
    public async Task A_crash_after_delivery_replays_the_mutation_with_the_same_idempotency_key()
    {
        var store = new FlakyRemoveStore { FailRemoves = 1 };
        var connectivity = new ConnectivityProvider();
        var delivered = new List<string>();
        var failures = new List<Exception>();
        using (var first = new PersistentMutationQueue(store, connectivity).RegisterHandler<NewTodo>("add", (_, m, _) => { delivered.Add(m.Id); return Task.CompletedTask; }, NewTodoInfo))
        {
            first.MutationFailed += (_, e) => failures.Add(e);
            await first.EnqueueAsync("add", new NewTodo("milk"));
        }

        Assert.Contains("may replay after a restart", Assert.Single(failures).Message);

        using var restarted = new PersistentMutationQueue(store, connectivity).RegisterHandler<NewTodo>("add", (_, m, _) => { delivered.Add(m.Id); return Task.CompletedTask; }, NewTodoInfo);
        await restarted.RestoreAsync();

        Assert.Equal(2, delivered.Count);          // at-least-once: delivered twice...
        Assert.Equal(delivered[0], delivered[1]);  // ...with the same key, so the server can deduplicate
        Assert.Equal(0, restarted.PendingCount);
    }

    [Fact]
    public async Task Retries_and_optimistic_updates_are_reported_as_diagnostic_events()
    {
        using var client = new QueryClient(TestUtil.NoRetry());
        var events = new List<DotNetQueryEvent>();
        client.DiagnosticEvent += e => { lock (events) { events.Add(e); } };
        var calls = 0;
        await client.FetchAsync<int>(QueryKey.Create("flaky"), _ => ++calls == 1 ? throw new TimeoutException("slow") : Task.FromResult(1), new QueryOptions { RetryCount = 1 });
        client.SetQueryData(QueryKey.Create("list"), 1);
        await client.CreateMutation<int, int>(
            (_, _) => throw new InvalidOperationException("rejected"),
            new() { ThrowOnError = false, OptimisticUpdates = [OptimisticUpdate.Create<int, int>(QueryKey.Create("list"), (n, v) => n + v)] })
            .MutateAsync(5);

        lock (events)
        {
            var retry = Assert.Single(events.OfType<QueryRetryEvent>());
            Assert.Equal(1, retry.FailureCount);
            Assert.IsType<TimeoutException>(retry.Error);
            Assert.Equal([QueryKey.Create("list")], Assert.Single(events.OfType<OptimisticUpdateAppliedEvent>()).Keys);
            var rollback = Assert.Single(events.OfType<OptimisticUpdateRolledBackEvent>());
            Assert.Equal([QueryKey.Create("list")], rollback.Restored);
            Assert.Empty(rollback.Refetched);
        }
    }

    [Fact]
    public async Task Diagnostics_can_omit_variables_and_redact_telemetry_keys()
    {
        using var client = new QueryClient(TestUtil.NoRetry(o =>
        {
            o.IncludeVariablesInDiagnostics = false;
            o.DiagnosticKeyFormatter = key => $"{key[0]}/***";
        }));
        var started = new List<MutationStartedEvent>();
        client.DiagnosticEvent += e => { if (e is MutationStartedEvent s) { started.Add(s); } };
        var tags = new List<string?>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == DotNetQueryDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => { lock (tags) { tags.Add(activity.GetTagItem("dotnetquery.query.key") as string); } },
        };
        ActivitySource.AddActivityListener(listener);

        await client.FetchAsync(QueryKey.Create("user", "alice@example.com"), _ => Task.FromResult(1));
        await client.MutateAsync<string, int>((_, _) => Task.FromResult(1), "password123");

        Assert.Null(Assert.Single(started).Variables);
        lock (tags)
        {
            Assert.Contains("user/***", tags);
            Assert.DoesNotContain(tags, t => t?.Contains("alice", StringComparison.Ordinal) == true);
        }
    }
}
