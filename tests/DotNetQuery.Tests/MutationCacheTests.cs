using DotNetQuery;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Tests;

public class MutationCacheTests
{
    private readonly FakeTimeProvider _time = new();

    private QueryClient CreateClient(Action<QueryClientOptions>? configure = null) => new(TestUtil.NoRetry(configure), _time);

    private static readonly QueryKey AddTodo = QueryKey.Create("todos", "add");

    [Fact]
    public async Task Tracks_pending_and_settled_runs()
    {
        using var client = CreateClient();
        var release = TestUtil.NewSignal<int>();
        var mutation = client.CreateMutation<string, int>((_, _) => release.Task, new() { MutationKey = AddTodo });

        var run = mutation.MutateAsync("milk");

        var pending = Assert.Single(client.Cache.Mutations.FindAll());
        Assert.Equal(MutationStatus.Pending, pending.Status);
        Assert.Equal("milk", pending.Variables);
        Assert.Equal(AddTodo, pending.MutationKey);
        Assert.Null(pending.SettledAt);
        Assert.Equal(1, client.Cache.Mutations.CountPending());

        release.SetResult(7);
        await run;

        var settled = client.Cache.Mutations.Find(pending.MutationId)!;
        Assert.Equal(MutationStatus.Success, settled.Status);
        Assert.Equal(7, settled.Data);
        Assert.Equal(_time.GetUtcNow(), settled.SettledAt);
        Assert.Equal(0, client.Cache.Mutations.CountPending());
    }

    [Fact]
    public async Task Records_failures_and_matches_diagnostic_ids()
    {
        using var client = CreateClient();
        long? startedId = null;
        client.DiagnosticEvent += e => { if (e is MutationStartedEvent s) { startedId = s.MutationId; } };
        var mutation = client.CreateMutation<int, int>((_, _) => throw new InvalidOperationException("rejected"), new() { ThrowOnError = false, RetryCount = 1 });

        await mutation.MutateAsync(1);

        var failed = Assert.Single(client.Cache.Mutations.FindAll(new MutationFilter { Status = MutationStatus.Error }));
        Assert.Equal(startedId, failed.MutationId);
        Assert.Equal("rejected", failed.Error!.Message);
        Assert.Equal(2, failed.FailureCount);
    }

    [Fact]
    public async Task Filters_by_key_scope_status_and_predicate()
    {
        using var client = CreateClient();
        await client.CreateMutation<int, int>((v, _) => Task.FromResult(v), new() { MutationKey = AddTodo }).MutateAsync(1);
        await client.CreateMutation<int, int>((v, _) => Task.FromResult(v), new() { MutationKey = QueryKey.Create("todos", "delete"), Scope = "todos" }).MutateAsync(2);
        await client.CreateMutation<int, int>((v, _) => Task.FromResult(v), new() { MutationKey = QueryKey.Create("users", "add") }).MutateAsync(3);

        Assert.Equal(2, client.Cache.Mutations.FindAll(new MutationFilter { MutationKey = QueryKey.Create("todos") }).Count);
        Assert.Single(client.Cache.Mutations.FindAll(new MutationFilter { MutationKey = AddTodo, Exact = true }));
        Assert.Single(client.Cache.Mutations.FindAll(new MutationFilter { Scope = "todos" }));
        Assert.Single(client.Cache.Mutations.FindAll(new MutationFilter { Predicate = m => (int)m.Variables! == 3 }));
        Assert.Equal(3, client.Cache.Mutations.FindAll(new MutationFilter { Status = MutationStatus.Success }).Count);
    }

    [Fact]
    public async Task Settled_runs_are_collected_after_the_retention_window()
    {
        using var client = CreateClient(o => o.MutationCacheTime = TimeSpan.FromMinutes(2));
        var changes = new List<MutationCacheChangeKind>();
        client.Cache.Mutations.Changed += c => { lock (changes) { changes.Add(c.Kind); } };
        await client.MutateAsync<int, int>((v, _) => Task.FromResult(v), 1);

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, client.Cache.Mutations.CollectGarbage());

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, client.Cache.Mutations.CollectGarbage());

        Assert.Equal(0, client.Cache.Mutations.Count);
        lock (changes)
        {
            Assert.Equal(MutationCacheChangeKind.Added, changes[0]);
            Assert.Contains(MutationCacheChangeKind.Updated, changes);
            Assert.Equal(MutationCacheChangeKind.Removed, changes[^1]);
        }
    }

    [Fact]
    public async Task Zero_retention_drops_runs_on_settle_and_Clear_keeps_pending_runs()
    {
        using var zero = CreateClient(o => o.MutationCacheTime = TimeSpan.Zero);
        await zero.MutateAsync<int, int>((v, _) => Task.FromResult(v), 1);
        Assert.Equal(0, zero.Cache.Mutations.Count);

        using var client = CreateClient();
        await client.MutateAsync<int, int>((v, _) => Task.FromResult(v), 1);
        var release = TestUtil.NewSignal<int>();
        var pending = client.MutateAsync<int, int>((_, _) => release.Task, 2);

        client.Cache.Mutations.Clear();

        var remaining = Assert.Single(client.Cache.Mutations.FindAll());
        Assert.Equal(MutationStatus.Pending, remaining.Status);
        release.SetResult(2);
        await pending;
    }

    [Fact]
    public async Task Runs_in_one_scope_execute_serially_in_start_order_across_instances()
    {
        using var client = CreateClient();
        var order = new List<string>();
        var active = 0;
        var maxActive = 0;
        async Task<string> Run(string name, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref active);
            lock (order)
            {
                maxActive = Math.Max(maxActive, now);
                order.Add(name);
            }

            await Task.Delay(5, ct);
            Interlocked.Decrement(ref active);
            return name;
        }

        var options = new MutationOptions<string, string> { Scope = "todo-1" };
        var create = client.CreateMutation<string, string>(Run, options);
        var rename = client.CreateMutation<string, string>(Run, options);

        var runs = Enumerable.Range(0, 10).Select(i => (i % 2 == 0 ? create : rename).MutateAsync($"op{i}")).ToArray();
        await Task.WhenAll(runs);

        Assert.Equal(1, maxActive);
        Assert.Equal(Enumerable.Range(0, 10).Select(i => $"op{i}"), order);
    }

    [Fact]
    public async Task A_waiting_run_is_paused_and_applies_its_optimistic_update_immediately()
    {
        using var client = CreateClient();
        var key = QueryKey.Create("count");
        client.SetQueryData(key, 0);
        var first = TestUtil.NewSignal<int>();
        var options = new MutationOptions<int, int>
        {
            Scope = "s",
            OptimisticUpdates = [OptimisticUpdate.Create<int, int>(key, (n, _) => n + 1)],
        };
        var blocking = client.CreateMutation<int, int>((_, _) => first.Task, options);
        var waiting = client.CreateMutation<int, int>((v, _) => Task.FromResult(v), options);

        var run1 = blocking.MutateAsync(1);
        var run2 = waiting.MutateAsync(2);

        await TestUtil.WaitUntilAsync(() => waiting.CurrentState.IsPaused);
        Assert.True(waiting.CurrentState.IsPending);
        Assert.Equal(2, client.GetQueryData<int>(key).Data); // both optimistic updates applied
        Assert.True(Assert.Single(client.Cache.Mutations.FindAll(new MutationFilter { Predicate = m => m.IsPaused })).IsPaused);

        first.SetResult(1);
        await Task.WhenAll(run1, run2);

        Assert.False(waiting.CurrentState.IsPaused);
        Assert.True(waiting.CurrentState.IsSuccess);
    }

    [Fact]
    public async Task Different_scopes_and_unscoped_runs_do_not_wait()
    {
        using var client = CreateClient();
        var never = TestUtil.NewSignal<int>();
        _ = client.CreateMutation<int, int>((_, _) => never.Task, new() { Scope = "a" }).MutateAsync(1);

        var otherScope = await client.CreateMutation<int, int>((v, _) => Task.FromResult(v), new() { Scope = "b" }).MutateAsync(2).WaitAsync(TimeSpan.FromSeconds(5));
        var unscoped = await client.CreateMutation<int, int>((v, _) => Task.FromResult(v)).MutateAsync(3).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(otherScope.IsSuccess);
        Assert.True(unscoped.IsSuccess);
        never.SetResult(0);
    }

    [Fact]
    public async Task A_failed_run_releases_its_scope()
    {
        using var client = CreateClient();
        var options = new MutationOptions<int, int> { Scope = "s", ThrowOnError = false };
        var failing = client.CreateMutation<int, int>((_, _) => throw new InvalidOperationException(), options);
        var next = client.CreateMutation<int, int>((v, _) => Task.FromResult(v), options);

        var results = await Task.WhenAll(failing.MutateAsync(1), next.MutateAsync(2)).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(results[0].IsError);
        Assert.True(results[1].IsSuccess);
    }

    [Fact]
    public async Task Cancelling_a_waiting_run_rolls_back_and_never_lets_the_next_run_overtake()
    {
        using var client = CreateClient();
        var key = QueryKey.Create("value");
        client.SetQueryData(key, "original");
        var first = TestUtil.NewSignal<string>();
        var thirdStarted = false;
        var options = new MutationOptions<string, string> { Scope = "s" };
        var blocking = client.CreateMutation<string, string>((_, _) => first.Task, options);
        var cancelled = client.CreateMutation<string, string>(
            (v, _) => Task.FromResult(v),
            options with { OptimisticUpdates = [OptimisticUpdate.Create<string, string>(key, (_, v) => v)] });
        var third = client.CreateMutation<string, string>((v, _) => { thirdStarted = true; return Task.FromResult(v); }, options);

        var run1 = blocking.MutateAsync("1");
        using var cts = new CancellationTokenSource();
        var run2 = cancelled.MutateAsync("optimistic", cts.Token);
        var run3 = third.MutateAsync("3");
        await TestUtil.WaitUntilAsync(() => cancelled.CurrentState.IsPaused && third.CurrentState.IsPaused);
        Assert.Equal("optimistic", client.GetQueryData<string>(key).Data);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run2);
        Assert.Equal("original", client.GetQueryData<string>(key).Data);
        await Task.Delay(50);
        Assert.False(thirdStarted); // still behind the first run

        first.SetResult("1");
        await Task.WhenAll(run1, run3);
        Assert.True(thirdStarted);
    }
}
