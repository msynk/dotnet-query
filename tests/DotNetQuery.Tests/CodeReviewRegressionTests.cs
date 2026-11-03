using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Serialization.Metadata;
using DotNetQuery;
using DotNetQuery.Persistence;
using DotNetQuery.Synchronization;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Tests;

/// <summary>Regression tests for the issues found by the full code review.</summary>
public class CodeReviewRegressionTests
{
    private sealed record NewTodo(string Title);

    private sealed record Todo(int Id, string Title);

    private static readonly TimeSpan Deadlock = TimeSpan.FromSeconds(10);
    private static readonly JsonTypeInfo<NewTodo> NewTodoInfo = (JsonTypeInfo<NewTodo>)TestUtil.WebJson.GetTypeInfo(typeof(NewTodo));
    private static readonly JsonTypeInfo<Todo> TodoInfo = (JsonTypeInfo<Todo>)TestUtil.WebJson.GetTypeInfo(typeof(Todo));
    private static readonly JsonTypeInfo<List<string>> ListInfo = (JsonTypeInfo<List<string>>)TestUtil.WebJson.GetTypeInfo(typeof(List<string>));

    /// <summary>A sync channel that keeps posted messages and delivers received ones only when told to.</summary>
    private sealed class ManualChannel : IQuerySyncChannel
    {
        public event Action<string>? MessageReceived;

        public List<string> Posted { get; } = [];

        public Task PostAsync(string message, CancellationToken cancellationToken = default)
        {
            lock (Posted)
            {
                Posted.Add(message);
            }

            return Task.CompletedTask;
        }

        public void Deliver(string message) => MessageReceived?.Invoke(message);
    }

    private sealed class Server(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    [Fact]
    public async Task A_replay_handler_can_enqueue_a_follow_up_mutation()
    {
        var connectivity = new ConnectivityProvider();
        using var queue = new PersistentMutationQueue(new InMemoryQueryPersister(), connectivity);
        var ran = new List<string>();
        queue
            .RegisterHandler<NewTodo>("create", async (todo, ct) =>
            {
                ran.Add("create " + todo.Title);
                await queue.EnqueueAsync("upload", todo, ct);
            }, NewTodoInfo)
            .RegisterHandler<NewTodo>("upload", (todo, _) =>
            {
                ran.Add("upload " + todo.Title);
                return Task.CompletedTask;
            }, NewTodoInfo);

        await queue.EnqueueAsync("create", new NewTodo("order")).WaitAsync(Deadlock);

        Assert.Equal(["create order", "upload order"], ran);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task A_restore_waiting_for_a_replay_does_not_block_its_handler_from_enqueueing()
    {
        var connectivity = new ConnectivityProvider();
        using var queue = new PersistentMutationQueue(new InMemoryQueryPersister(), connectivity);
        var handlerStarted = TestUtil.NewSignal();
        var release = TestUtil.NewSignal();
        queue
            .RegisterHandler<NewTodo>("create", async (todo, ct) =>
            {
                handlerStarted.TrySetResult();
                await release.Task;
                await queue.EnqueueAsync("upload", todo, ct);
            }, NewTodoInfo)
            .RegisterHandler<NewTodo>("upload", (_, _) => Task.CompletedTask, NewTodoInfo);

        var enqueue = queue.EnqueueAsync("create", new NewTodo("order"));
        await handlerStarted.Task.WaitAsync(Deadlock);
        var restore = queue.RestoreAsync();
        await Task.Delay(50); // the restore is now waiting for the replay
        release.SetResult();

        await Task.WhenAll(enqueue, restore).WaitAsync(Deadlock);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task A_retried_dead_letter_stays_behind_later_mutations_after_a_restore()
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryQueryPersister();
        var connectivity = new ConnectivityProvider();
        var options = new PersistentMutationQueueOptions { UseDeadLetterQueue = true, TimeProvider = time };
        var fail = true;
        var replayed = new List<string>();
        PersistentMutationQueue Create() => new PersistentMutationQueue(store, connectivity, options).RegisterHandler<NewTodo>("save", (todo, _) =>
        {
            if (fail)
            {
                throw new InvalidOperationException("conflict");
            }

            replayed.Add(todo.Title);
            return Task.CompletedTask;
        }, NewTodoInfo);

        string[] order;
        using (var queue = Create())
        {
            await queue.EnqueueAsync("save", new NewTodo("update X")); // fails online: dead-lettered
            var dead = Assert.Single(queue.DeadLetters).Mutation.Id;
            connectivity.SetOnline(false);
            time.Advance(TimeSpan.FromSeconds(1));
            await queue.EnqueueAsync("save", new NewTodo("first"));
            time.Advance(TimeSpan.FromSeconds(1));
            await queue.EnqueueAsync("save", new NewTodo("delete X"));
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.True(await queue.RetryDeadLetterAsync(dead));
            order = queue.Pending.Select(p => p.Id).ToArray();
            Assert.Equal(dead, order[^1]);

            await queue.RestoreAsync(); // a merge keeps the order too
            Assert.Equal(order, queue.Pending.Select(p => p.Id));
        }

        using var restarted = Create();
        Assert.Equal(3, await restarted.RestoreAsync());
        Assert.Equal(order, restarted.Pending.Select(p => p.Id));

        fail = false;
        connectivity.SetOnline(true);
        await restarted.FlushAsync();
        Assert.Equal(["first", "delete X", "update X"], replayed);
    }

    [Fact]
    public async Task A_validator_added_by_the_conditional_handler_is_not_resent_after_a_token_refresh()
    {
        var token = "old";
        var server = new Server(request =>
        {
            if (request.Headers.Authorization?.Parameter != token)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            if (request.Headers.IfNoneMatch.Any(t => t.Tag == "\"v1\""))
            {
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[\"a\"]", Encoding.UTF8, "application/json") };
            response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            return response;
        });
        var current = "old";
        var bearer = new BearerTokenHandler(_ => ValueTask.FromResult<string?>(current), _ => ValueTask.FromResult<string?>(current = "fresh"))
        {
            InnerHandler = new ConditionalRequestHandler { InnerHandler = server },
        };
        using var http = new HttpClient(bearer) { BaseAddress = new Uri("https://api.test/") };
        Assert.Equal("[\"a\"]", await http.GetStringAsync("todos")); // remembered with its ETag

        token = "fresh"; // the old token expired
        using var response = await http.GetAsync("todos");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[\"a\"]", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Out_of_order_messages_from_one_sender_never_replace_newer_data()
    {
        var time = new FakeTimeProvider();
        var key = QueryKey.Create("todos");
        using var a = new QueryClient(TestUtil.NoRetry(), time);
        using var b = new QueryClient(TestUtil.NoRetry(), time);
        var fromA = new ManualChannel();
        var toB = new ManualChannel();
        using var syncA = new QuerySynchronizer(a, fromA).Register(key, ListInfo).Start();
        using var syncB = new QuerySynchronizer(b, toB).Register(key, ListInfo).Start();

        a.SetQueryData(key, new List<string> { "t1" });
        time.Advance(TimeSpan.FromSeconds(1));
        a.SetQueryData(key, new List<string> { "t2" });
        Assert.Equal(2, fromA.Posted.Count);

        toB.Deliver(fromA.Posted[1]);
        toB.Deliver(fromA.Posted[0]); // delivered late

        Assert.Equal(["t2"], b.GetQueryData<List<string>>(key).Data);
    }

    [Fact]
    public void Prefix_messages_reach_queries_cached_before_and_after_start()
    {
        var hub = new InMemoryQuerySyncHub();
        var prefix = QueryKey.Create("todo");
        using var a = new QueryClient(TestUtil.NoRetry());
        using var b = new QueryClient(TestUtil.NoRetry());
        b.SetQueryData(prefix.Append(1), new Todo(1, "cached before start"));
        using var syncA = new QuerySynchronizer(a, hub.CreateChannel()).RegisterPrefix(prefix, TodoInfo).Start();
        using var syncB = new QuerySynchronizer(b, hub.CreateChannel()).RegisterPrefix(prefix, TodoInfo).Start();
        b.SetQueryData(prefix.Append(2), new Todo(2, "cached after start"));

        a.SetQueryData(prefix.Append(1), new Todo(1, "one"));
        a.SetQueryData(prefix.Append(2), new Todo(2, "two"));
        a.SetQueryData(prefix.Append(3), new Todo(3, "not held by b"));

        Assert.Equal("one", b.GetQueryData<Todo>(prefix.Append(1)).Data!.Title);
        Assert.Equal("two", b.GetQueryData<Todo>(prefix.Append(2)).Data!.Title);
        Assert.Null(b.Cache.Describe(prefix.Append(3)));

        b.RemoveQueries(QueryFilter.ForKey(prefix.Append(2), exact: true));
        b.SetQueryData(prefix.Append(2), new Todo(2, "cached again"));
        a.SetQueryData(prefix.Append(2), new Todo(2, "two again"));
        Assert.Equal("two again", b.GetQueryData<Todo>(prefix.Append(2)).Data!.Title);
    }

    [Fact]
    public async Task A_batch_function_returning_no_dictionary_fails_its_waiters_instead_of_hanging()
    {
        var loader = new BatchLoader<int, string>((_, _) => Task.FromResult<IReadOnlyDictionary<int, string>>(null!), new BatchLoaderOptions { MaxBatchSize = 1 });

        var first = loader.LoadAsync(1);
        var second = loader.LoadAsync(2);

        await Assert.ThrowsAsync<InvalidOperationException>(() => first.WaitAsync(Deadlock));
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.WaitAsync(Deadlock));
    }

    [Fact]
    public async Task A_batch_whose_lookup_throws_fails_its_waiters()
    {
        var loader = new BatchLoader<int, string>((_, _) => Task.FromResult<IReadOnlyDictionary<int, string>>(new ThrowingDictionary()));

        await Assert.ThrowsAsync<FormatException>(() => loader.LoadAsync(1).WaitAsync(Deadlock));
    }

    [Fact]
    public async Task Chunks_of_a_large_burst_are_fetched_concurrently()
    {
        var started = 0;
        var bothStarted = TestUtil.NewSignal();
        var loader = new BatchLoader<int, int>(async (ids, _) =>
        {
            if (Interlocked.Increment(ref started) == 2)
            {
                bothStarted.TrySetResult();
            }

            await bothStarted.Task; // a sequential loader would never start the second chunk
            return ids.ToDictionary(i => i);
        }, new BatchLoaderOptions { MaxBatchSize = 2 });

        var values = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => loader.LoadAsync(i))).WaitAsync(Deadlock);

        Assert.Equal([0, 1, 2, 3], values);
    }

    [Fact]
    public async Task A_narrow_filter_that_matches_nothing_does_not_invalidate_declared_dependents()
    {
        var options = TestUtil.NoRetry(o => o.DefaultStaleTime = TimeSpan.FromMinutes(5))
            .AddInvalidationDependency(QueryKey.Create("todos", "open"), QueryKey.Create("stats", "open"));
        using var client = new QueryClient(options);
        var statsKey = QueryKey.Create("stats", "open");
        var statsCalls = 0;
        using var stats = client.Watch<int>(statsKey);
        await client.FetchAsync(statsKey, _ => Task.FromResult(++statsCalls));

        await client.InvalidateQueriesAsync(QueryFilter.ForKey(QueryKey.Create("todos"), exact: true));
        await client.InvalidateQueriesAsync(new QueryFilter { Key = QueryKey.Create("todos"), Type = QueryTypeFilter.Inactive });

        Assert.Equal(1, statsCalls);
        Assert.False(client.Cache.Describe(statsKey)!.IsStale);

        await client.InvalidateQueriesAsync(QueryFilter.ForKey(QueryKey.Create("todos"))); // a plain prefix filter still does
        Assert.Equal(2, statsCalls);
    }

    [Fact]
    public async Task A_deduplicated_mutation_does_not_run_state_handlers_under_its_lock()
    {
        using var client = new QueryClient(TestUtil.NoRetry());
        var mutation = client.CreateMutation<int, int>((v, _) => Task.FromResult(v), new() { DeduplicatePending = true });
        var nestedCompleted = false;
        var nested = 0;
        mutation.StateChanged += state =>
        {
            // Another thread mutates while this handler blocks (a UI dispatcher waiting on a worker, say).
            if (state is { Status: MutationStatus.Pending, Variables: 1 } && Interlocked.Exchange(ref nested, 1) == 0)
            {
                nestedCompleted = Task.Run(() => mutation.MutateAsync(2)).Wait(TimeSpan.FromSeconds(5));
            }
        };

        await mutation.MutateAsync(1).WaitAsync(Deadlock);

        Assert.True(nestedCompleted);
    }

    [Fact]
    public async Task A_throwing_retry_delay_fails_the_mutation_with_its_own_error()
    {
        using var client = new QueryClient(TestUtil.NoRetry());
        var calls = 0;
        var mutation = client.CreateMutation<int, int>(
            (_, _) =>
            {
                calls++;
                throw new HttpRequestException("boom");
            },
            new() { RetryCount = 2, RetryDelay = (_, _) => throw new InvalidOperationException("bad strategy") });

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => mutation.MutateAsync(1));

        Assert.Equal("boom", error.Message);
        Assert.Equal(1, calls);
        Assert.Equal(1, mutation.CurrentState.FailureCount);
    }

    [Fact]
    public async Task A_throwing_failure_classifier_does_not_leave_the_half_open_trial_stuck()
    {
        var time = new FakeTimeProvider();
        var healthy = false;
        var throwing = false;
        var breaker = new CircuitBreakerHandler(new CircuitBreakerOptions
        {
            FailureThreshold = 1,
            BreakDuration = TimeSpan.FromSeconds(10),
            TimeProvider = time,
            IsFailure = (response, _) => throwing ? throw new InvalidOperationException("classifier bug") : response?.StatusCode is HttpStatusCode.ServiceUnavailable,
        })
        {
            InnerHandler = new Server(_ => new HttpResponseMessage(healthy ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)),
        };
        using var http = new HttpClient(breaker) { BaseAddress = new Uri("https://api.test/") };
        (await http.GetAsync("x")).Dispose();
        Assert.Equal(CircuitState.Open, breaker.State);

        time.Advance(TimeSpan.FromSeconds(10));
        healthy = throwing = true;
        using var trial = await http.GetAsync("x");

        Assert.Equal(HttpStatusCode.OK, trial.StatusCode);
        Assert.Equal(CircuitState.Closed, breaker.State);
        using var next = await http.GetAsync("x");
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Fact]
    public async Task Restore_survives_distinct_cached_keys_with_the_same_text()
    {
        var prefix = QueryKey.Create("todo");
        var store = new InMemoryQueryPersister();
        using (var first = new QueryClient(TestUtil.NoRetry()))
        using (var manager = new QueryPersistenceManager(first, store).RegisterPrefix(prefix, TodoInfo))
        {
            first.SetQueryData(QueryKey.Create("todo", 5), new Todo(5, "persisted"));
            await manager.PersistAllAsync();
        }

        using var second = new QueryClient(TestUtil.NoRetry());
        second.SetQueryData(QueryKey.Create("todo", 5), new Todo(5, "int key"));
        second.SetQueryData(QueryKey.Create("todo", 5L), new Todo(5, "long key"));
        using var restoring = new QueryPersistenceManager(second, store).RegisterPrefix(prefix, TodoInfo);

        var restored = await restoring.RestoreAllAsync();

        Assert.Equal(1, restored);
    }

    [Fact]
    public async Task A_value_type_selector_reruns_only_when_the_value_changes()
    {
        using var client = new QueryClient(TestUtil.NoRetry());
        var key = QueryKey.Create("count");
        var selects = 0;
        using var observer = client.Watch<int, string>(key, n =>
        {
            selects++;
            return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        });

        await client.FetchAsync(key, _ => Task.FromResult(5));
        await client.FetchAsync(key, _ => Task.FromResult(5), forceRefetch: true);
        Assert.Equal(1, selects);

        await client.FetchAsync(key, _ => Task.FromResult(6), forceRefetch: true);
        Assert.Equal(2, selects);
        Assert.Equal("6", observer.CurrentState.Data);
    }

    private sealed class ThrowingDictionary : Dictionary<int, string>, IReadOnlyDictionary<int, string>
    {
        bool IReadOnlyDictionary<int, string>.TryGetValue(int key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out string value) =>
            throw new FormatException("corrupt response");
    }
}
