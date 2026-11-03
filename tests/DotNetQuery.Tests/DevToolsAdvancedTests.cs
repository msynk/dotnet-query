using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using DotNetQuery;
using DotNetQuery.DevTools;
using DotNetQuery.Persistence;
using Xunit;

namespace DotNetQuery.Tests;

public class DevToolsAdvancedTests
{
    [Fact]
    public async Task Timeline_explains_why_a_query_fetched_retried_and_was_invalidated()
    {
        using var client = new QueryClient(TestUtil.NoRetry());
        using var devtools = new QueryDevTools(client);
        var key = QueryKey.Create("timeline");
        var calls = 0;

        await client.FetchAsync<int>(key, _ => ++calls == 1 ? throw new TimeoutException() : Task.FromResult(1), new QueryOptions { RetryCount = 1 });
        await client.InvalidateQueriesAsync(key);
        await client.FetchAsync(QueryKey.Create("other"), _ => Task.FromResult(2));

        var timeline = devtools.Timeline(key);
        Assert.Collection(
            timeline,
            e => Assert.IsType<QueryStartedEvent>(e),
            e => Assert.IsType<QueryRetryEvent>(e),
            e => Assert.IsType<QueryCompletedEvent>(e),
            e => Assert.IsType<QueryInvalidatedEvent>(e));
    }

    [Fact]
    public async Task Profiles_record_fetches_failures_retries_and_durations_per_query()
    {
        using var client = new QueryClient(TestUtil.NoRetry());
        using var devtools = new QueryDevTools(client);
        var calls = 0;

        await client.FetchAsync<int>(QueryKey.Create("p"), _ => ++calls == 1 ? throw new TimeoutException() : Task.FromResult(1), new QueryOptions { RetryCount = 1 });
        await client.FetchAsync<int>(QueryKey.Create("p"), _ => throw new InvalidOperationException(), forceRefetch: true);

        var profile = Assert.Single(devtools.ProfileQueries());
        Assert.Equal(2, profile.Fetches);
        Assert.Equal(1, profile.Failures);
        Assert.Equal(1, profile.Retries);
        Assert.True(profile.MaxDurationMs >= profile.AverageDurationMs);
    }

    [Fact]
    public void Key_tree_groups_queries_by_segment()
    {
        using var client = new QueryClient(TestUtil.NoRetry());
        using var devtools = new QueryDevTools(client);
        client.SetQueryData(QueryKey.Create("todos"), 0);
        client.SetQueryData(QueryKey.Create("todos", 1), 1);
        client.SetQueryData(QueryKey.Create("todos", 2), 2);
        client.SetQueryData(QueryKey.Create("users", "me"), 3);

        var tree = devtools.KeyTree();

        Assert.Equal(["\"todos\"", "\"users\""], tree.Select(n => n.Segment));
        var todos = tree[0];
        Assert.NotNull(todos.Query);
        Assert.Equal(3, todos.QueryCount);
        Assert.Equal(["1", "2"], todos.Children.Select(c => c.Segment));
        Assert.Null(tree[1].Query);
        Assert.Equal(QueryKey.Create("users", "me"), Assert.Single(tree[1].Children).Key);
    }

    [Fact]
    public void Snapshots_support_time_travel()
    {
        using var client = new QueryClient(TestUtil.NoRetry());
        using var devtools = new QueryDevTools(client);
        client.SetQueryData(QueryKey.Create("a"), 1);
        var first = devtools.TakeSnapshot("first");
        client.SetQueryData(QueryKey.Create("a"), 2);
        client.SetQueryData(QueryKey.Create("b"), 3);
        devtools.TakeSnapshot("second");

        devtools.RestoreSnapshot(first);

        Assert.Equal(1, client.GetQueryData<int>(QueryKey.Create("a")).Data);
        Assert.False(client.Cache.TryGetState<int>(QueryKey.Create("b"), out _));
        Assert.Equal(["first", "second"], devtools.Snapshots.Select(s => s.Label));
    }

    [Fact]
    public async Task Mutation_history_records_optimistic_writes_and_rollbacks()
    {
        using var client = new QueryClient(TestUtil.NoRetry());
        using var devtools = new QueryDevTools(client);
        client.SetQueryData(QueryKey.Create("list"), 1);

        await client.CreateMutation<int, int>(
            (_, _) => throw new InvalidOperationException("no"),
            new() { ThrowOnError = false, OptimisticUpdates = [OptimisticUpdate.Create<int, int>(QueryKey.Create("list"), (n, v) => n + v)] })
            .MutateAsync(1);

        var record = Assert.Single(devtools.RecentMutations());
        Assert.Equal([QueryKey.Create("list")], record.OptimisticKeys);
        Assert.Equal([QueryKey.Create("list")], record.RolledBackKeys);
        Assert.Empty(record.RefetchedKeys);
    }

    private sealed record NewTodo(string Title);

    [Fact]
    public async Task An_attached_offline_queue_can_be_inspected()
    {
        using var client = new QueryClient(TestUtil.NoRetry());
        using var devtools = new QueryDevTools(client);
        var connectivity = new ConnectivityProvider();
        connectivity.SetOnline(false);
        var info = (JsonTypeInfo<NewTodo>)TestUtil.WebJson.GetTypeInfo(typeof(NewTodo));
        using var queue = new PersistentMutationQueue(new InMemoryQueryPersister(), connectivity).RegisterHandler<NewTodo>("add", (_, _) => Task.CompletedTask, info);
        var changes = 0;
        devtools.Changed += () => changes++;
        devtools.AttachQueue(queue);

        await queue.EnqueueAsync("add", new NewTodo("x"));

        Assert.Equal("add", Assert.Single(devtools.QueuedMutations).MutationKey);
        Assert.Empty(devtools.DeadLetters);
        Assert.True(changes >= 2);
    }
}
