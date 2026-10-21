using DotNetQuery;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static DotNetQuery.Tests.TestUtil;

namespace DotNetQuery.Tests;

/// <summary>Mutation semantics: callbacks never re-run the mutation, rollback on any failure, declarative updates.</summary>
public class MutationBehaviorTests
{
    private static QueryClient CreateClient() => new(NoRetry(), new FakeTimeProvider());

    [Fact]
    public async Task OnSuccess_throwing_does_not_re_run_the_mutation_function()
    {
        using var client = CreateClient();
        var calls = 0;

        var result = await client.MutateAsync<int, int>(
            (v, _) => { Interlocked.Increment(ref calls); return Task.FromResult(v); },
            1,
            new MutationOptions<int, int>
            {
                RetryCount = 3,
                ThrowOnError = false,
                OnSuccess = (_, _, _, _) => throw new InvalidOperationException("callback bug"),
            });

        Assert.Equal(1, calls);
        Assert.True(result.IsError);
        Assert.IsType<InvalidOperationException>(result.Error);
    }

    [Fact]
    public async Task OnMutate_throwing_settles_as_error_instead_of_hanging_pending()
    {
        using var client = CreateClient();
        var mutation = client.CreateMutation<int, int>(
            (v, _) => Task.FromResult(v),
            new MutationOptions<int, int>
            {
                ThrowOnError = false,
                OnMutate = (_, _) => throw new InvalidOperationException("bad optimistic update"),
            });

        var result = await mutation.MutateAsync(1);

        Assert.True(result.IsError);
        Assert.False(mutation.CurrentState.IsPending);
    }

    [Fact]
    public async Task Cancellation_rolls_back_optimistic_updates_and_rethrows()
    {
        using var client = CreateClient();
        var key = QueryKey.Create("todos");
        client.SetQueryData(key, new List<string> { "a" });
        var started = NewSignal();
        var settled = false;
        using var cts = new CancellationTokenSource();

        var run = client.MutateAsync<string, string>(
            async (title, ct) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return title;
            },
            "b",
            new MutationOptions<string, string>
            {
                OptimisticUpdates = [OptimisticUpdate.Create<string, List<string>>(key, (todos, title) => [.. todos ?? [], title])],
                OnSettled = (_, _, _, _, ct) => { settled = !ct.IsCancellationRequested; return ValueTask.CompletedTask; },
            },
            cts.Token);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["a", "b"], client.GetQueryData<List<string>>(key).Data!);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal(["a"], client.GetQueryData<List<string>>(key).Data!);
        Assert.True(settled, "OnSettled runs with a live token after cancellation.");
    }

    [Fact]
    public async Task Optimistic_update_is_applied_then_reconciled_via_invalidation()
    {
        using var client = CreateClient();
        var key = QueryKey.Create("todos");
        var server = new List<string> { "a" };
        using var observer = client.Watch<List<string>>(key);
        await client.FetchAsync(key, _ => Task.FromResult(server.ToList()));
        List<string>? seenDuringMutation = null;

        var result = await client.MutateAsync<string, string>(
            (title, _) =>
            {
                seenDuringMutation = client.GetQueryData<List<string>>(key).Data;
                server.Add(title + "!"); // the server normalizes the value
                return Task.FromResult(title);
            },
            "b",
            new MutationOptions<string, string>
            {
                OptimisticUpdates = [OptimisticUpdate.Create<string, List<string>>(key, (todos, title) => [.. todos ?? [], title])],
                Invalidates = [key],
            });

        Assert.True(result.IsSuccess);
        Assert.Equal(["a", "b"], seenDuringMutation!);
        Assert.Equal(["a", "b!"], client.GetQueryData<List<string>>(key).Data!);
    }

    [Fact]
    public async Task Failed_mutation_rolls_back_with_the_original_timestamp()
    {
        var time = new FakeTimeProvider();
        using var client = new QueryClient(NoRetry(o => o.DefaultStaleTime = TimeSpan.FromMinutes(5)), time);
        var key = QueryKey.Create("count");
        client.SetQueryData(key, 1);
        var originalTimestamp = client.GetQueryData<int>(key).DataUpdatedAt;
        time.Advance(TimeSpan.FromMinutes(1));

        var result = await client.MutateAsync<int, int>(
            (_, _) => Task.FromException<int>(new InvalidOperationException()),
            5,
            new MutationOptions<int, int>
            {
                ThrowOnError = false,
                OptimisticUpdates = [OptimisticUpdate.Create<int, int>(key, (current, add) => current + add)],
            });

        Assert.True(result.IsError);
        var rolledBack = client.GetQueryData<int>(key);
        Assert.Equal(1, rolledBack.Data);
        Assert.Equal(originalTimestamp, rolledBack.DataUpdatedAt);
    }

    [Fact]
    public async Task Optimistic_update_cancels_an_in_flight_refetch_so_it_cannot_overwrite()
    {
        using var client = CreateClient();
        var key = QueryKey.Create("race");
        await client.FetchAsync(key, _ => Task.FromResult("v1"));
        var started = NewSignal();
        var slowRefetch = client.FetchAsync(key, Gated(started, NewSignal<string>().Task), forceRefetch: true);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await client.MutateAsync<string, string>(
            (v, _) => Task.FromResult(v),
            "optimistic",
            new MutationOptions<string, string>
            {
                OptimisticUpdates = [OptimisticUpdate.Create<string, string>(key, (_, v) => v)],
            });

        await slowRefetch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("optimistic", client.GetQueryData<string>(key).Data);
    }

    [Fact]
    public async Task Invalidates_runs_even_when_the_mutation_fails()
    {
        using var client = CreateClient();
        var key = QueryKey.Create("reconcile");
        var fetches = 0;
        using var observer = client.Watch<int>(key);
        await client.FetchAsync(key, _ => Task.FromResult(Interlocked.Increment(ref fetches)));

        await client.MutateAsync<int, int>(
            (_, _) => Task.FromException<int>(new InvalidOperationException()),
            0,
            new MutationOptions<int, int> { ThrowOnError = false, Invalidates = [key] });

        Assert.Equal(2, fetches);
    }

    [Fact]
    public async Task Reusable_mutation_state_tracks_the_latest_run()
    {
        using var client = CreateClient();
        var first = NewSignal<int>();
        var second = NewSignal<int>();
        var mutation = client.CreateMutation<int, int>((v, _) => v == 1 ? first.Task : second.Task);

        var run1 = mutation.MutateAsync(1);
        var run2 = mutation.MutateAsync(2);
        second.SetResult(20);
        await run2;
        Assert.Equal(20, mutation.CurrentState.Data);

        first.SetResult(10); // the older run finishes last but must not clobber the newer state
        Assert.Equal(10, (await run1).Data);
        Assert.Equal(20, mutation.CurrentState.Data);
        Assert.Equal(2, mutation.CurrentState.Variables);
    }

    [Fact]
    public async Task Mutation_events_carry_id_key_and_variables()
    {
        using var client = CreateClient();
        var events = new List<DotNetQueryEvent>();
        client.DiagnosticEvent += e => { lock (events) { events.Add(e); } };
        var mutationKey = QueryKey.Create("todos", "add");

        await client.MutateAsync<string, string>((v, _) => Task.FromResult(v), "milk", new MutationOptions<string, string> { MutationKey = mutationKey });

        lock (events)
        {
            var started = Assert.Single(events.OfType<MutationStartedEvent>());
            var completed = Assert.Single(events.OfType<MutationCompletedEvent>());
            Assert.Equal(started.MutationId, completed.MutationId);
            Assert.True(started.MutationId > 0);
            Assert.Equal(mutationKey, started.MutationKey);
            Assert.Equal("milk", started.Variables);
            Assert.True(completed.Succeeded);
        }
    }

    [Fact]
    public async Task Failure_count_counts_every_failed_attempt()
    {
        using var client = CreateClient();
        var result = await client.MutateAsync<int, int>(
            (_, _) => Task.FromException<int>(new InvalidOperationException()),
            0,
            new MutationOptions<int, int> { RetryCount = 2, ThrowOnError = false });

        Assert.Equal(3, result.State.FailureCount);
    }
}
