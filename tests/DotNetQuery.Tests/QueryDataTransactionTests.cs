using DotNetQuery;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Tests;

public class QueryDataTransactionTests
{
    private readonly FakeTimeProvider _time = new();

    private QueryClient CreateClient() => new(TestUtil.NoRetry(o => o.DefaultStaleTime = TimeSpan.FromMinutes(1)), _time);

    private static readonly QueryKey A = QueryKey.Create("a");
    private static readonly QueryKey B = QueryKey.Create("b");
    private static readonly QueryKey C = QueryKey.Create("c");

    [Fact]
    public async Task Applies_every_write_and_rolls_back_exactly()
    {
        using var client = CreateClient();
        await client.FetchAsync(A, _ => Task.FromResult(1));
        await client.InvalidateQueriesAsync(A); // stale before the transaction
        var before = client.GetQueryData<int>(A).State;
        _time.Advance(TimeSpan.FromSeconds(3));

        var commit = client.SetQueriesData(tx => tx
            .Update<int>(A, n => n + 10)
            .Set(B, "new")
            .Update<List<int>>(C, list => [.. list ?? [], 7]));

        Assert.Equal([A, B, C], commit.Keys);
        Assert.Equal(11, client.GetQueryData<int>(A).Data);
        Assert.Equal("new", client.GetQueryData<string>(B).Data);
        Assert.Equal([7], client.GetQueryData<List<int>>(C).Data);
        Assert.False(client.Cache.Describe(A)!.IsStale);

        var conflicts = commit.Rollback();

        Assert.Empty(conflicts);
        Assert.Equal(before, client.GetQueryData<int>(A).State);
        Assert.True(client.Cache.Describe(A)!.IsStale);
        Assert.False(client.GetQueryData<string>(B).HasData);
        Assert.False(client.GetQueryData<List<int>>(C).HasData);
        Assert.Empty(commit.Rollback()); // only the first call has an effect
    }

    [Fact]
    public void Observers_never_see_a_partial_transaction()
    {
        using var client = CreateClient();
        client.SetQueryData(A, 0);
        client.SetQueryData(B, 0);
        using var observer = client.Watch<int>(A);
        var mismatches = 0;
        observer.StateChanged += state =>
        {
            if (state.Data != client.GetQueryData<int>(B).Data)
            {
                Interlocked.Increment(ref mismatches);
            }
        };

        for (var i = 1; i <= 50; i++)
        {
            var value = i;
            client.SetQueriesData(tx => tx.Set(A, value).Set(B, value));
        }

        Assert.Equal(0, mismatches);
    }

    [Fact]
    public void A_throwing_updater_writes_nothing()
    {
        using var client = CreateClient();
        client.SetQueryData(A, 1);

        Assert.Throws<InvalidOperationException>(() => client.SetQueriesData(tx => tx
            .Set(A, 2)
            .Update<int>(B, _ => throw new InvalidOperationException("bad"))));

        Assert.Equal(1, client.GetQueryData<int>(A).Data);
        Assert.False(client.GetQueryData<int>(B).HasData);
    }

    [Fact]
    public void Writes_to_the_same_key_compose_in_order_and_types_must_match()
    {
        using var client = CreateClient();

        client.SetQueriesData(tx => tx.Set(A, 2).Update<int>(A, n => n * 10).Update<int>(A, n => n + 1));

        Assert.Equal(21, client.GetQueryData<int>(A).Data);
        Assert.Throws<InvalidOperationException>(() => client.SetQueriesData(tx => tx.Set(B, 1).Set(B, "one")));
    }

    [Fact]
    public void Rollback_skips_keys_written_after_the_commit()
    {
        using var client = CreateClient();
        client.SetQueryData(A, 1);
        client.SetQueryData(B, 1);
        var commit = client.SetQueriesData(tx => tx.Set(A, 2).Set(B, 2));
        client.SetQueryData(B, 3); // a newer write on top

        var conflicts = commit.Rollback();

        Assert.Equal([B], conflicts);
        Assert.Equal(1, client.GetQueryData<int>(A).Data);
        Assert.Equal(3, client.GetQueryData<int>(B).Data);
    }

    [Fact]
    public async Task Concurrent_writers_never_lose_updates()
    {
        using var client = CreateClient();
        client.SetQueryData(A, 0);
        client.SetQueryData(B, 0);

        var transactions = Enumerable.Range(0, 200).Select(_ => Task.Run(() =>
            client.SetQueriesData(tx => tx.Update<int>(A, n => n + 1).Update<int>(B, n => n + 1))));
        var singles = Enumerable.Range(0, 200).Select(_ => Task.Run(() => client.SetQueryData<int>(A, n => n + 1)));
        await Task.WhenAll(transactions.Cast<Task>().Concat(singles));

        Assert.Equal(400, client.GetQueryData<int>(A).Data);
        Assert.Equal(200, client.GetQueryData<int>(B).Data);
    }

    [Fact]
    public async Task Transactions_over_the_same_keys_in_opposite_order_do_not_deadlock()
    {
        using var client = CreateClient();
        var keys = Enumerable.Range(0, 8).Select(i => QueryKey.Create("k", i)).ToArray();
        foreach (var key in keys)
        {
            client.SetQueryData(key, 0);
        }

        var forward = Task.Run(() =>
        {
            for (var i = 0; i < 300; i++)
            {
                client.SetQueriesData(tx => { foreach (var key in keys) { tx.Update<int>(key, n => n + 1); } });
            }
        });
        var backward = Task.Run(() =>
        {
            for (var i = 0; i < 300; i++)
            {
                client.SetQueriesData(tx => { foreach (var key in Enumerable.Reverse(keys)) { tx.Update<int>(key, n => n + 1); } });
            }
        });

        await Task.WhenAll(forward, backward).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.All(keys, key => Assert.Equal(600, client.GetQueryData<int>(key).Data));
    }

    [Fact]
    public async Task Filter_overload_patches_matching_queries_of_the_type_that_hold_data()
    {
        using var client = CreateClient();
        await client.FetchAsync(QueryKey.Create("todos", "open"), _ => Task.FromResult(new List<string> { "x", "old" }));
        await client.FetchAsync(QueryKey.Create("todos", "all"), _ => Task.FromResult(new List<string> { "old" }));
        await client.FetchAsync(QueryKey.Create("todos", "count"), _ => Task.FromResult(2));
        using var empty = client.Watch<List<string>>(QueryKey.Create("todos", "empty"));

        var commit = client.SetQueriesData<List<string>>(
            QueryFilter.ForKey(QueryKey.Create("todos")),
            list => list!.Select(t => t == "old" ? "new" : t).ToList());

        Assert.Equal(2, commit.Keys.Count);
        Assert.Equal(["x", "new"], client.GetQueryData<List<string>>(QueryKey.Create("todos", "open")).Data);
        Assert.Equal(["new"], client.GetQueryData<List<string>>(QueryKey.Create("todos", "all")).Data);
        Assert.Equal(2, client.GetQueryData<int>(QueryKey.Create("todos", "count")).Data);
        Assert.False(client.GetQueryData<List<string>>(QueryKey.Create("todos", "empty")).HasData);
    }

    [Fact]
    public async Task Multi_key_optimistic_updates_apply_atomically_and_roll_back_on_failure()
    {
        using var client = CreateClient();
        client.SetQueryData(A, new List<string> { "a" });
        client.SetQueryData(B, 1);
        using var observer = client.Watch<List<string>>(A);
        var torn = 0;
        observer.StateChanged += state =>
        {
            // The list and the counter are always updated together.
            if (state.Data!.Count != client.GetQueryData<int>(B).Data)
            {
                Interlocked.Increment(ref torn);
            }
        };
        var release = TestUtil.NewSignal();
        var mutation = client.CreateMutation<string, string>(
            async (_, ct) => { await release.Task.WaitAsync(ct); throw new InvalidOperationException("rejected"); },
            new MutationOptions<string, string>
            {
                ThrowOnError = false,
                OptimisticUpdates =
                [
                    OptimisticUpdate.Create<string, List<string>>(A, (list, added) => [.. list ?? [], added]),
                    OptimisticUpdate.Create<string, int>(B, (count, _) => count + 1),
                ],
            });

        var run = mutation.MutateAsync("b");
        await TestUtil.WaitUntilAsync(() => client.GetQueryData<int>(B).Data == 2);
        Assert.Equal(["a", "b"], client.GetQueryData<List<string>>(A).Data);

        release.SetResult();
        var result = await run;

        Assert.True(result.IsError);
        Assert.Equal(["a"], client.GetQueryData<List<string>>(A).Data);
        Assert.Equal(1, client.GetQueryData<int>(B).Data);
        Assert.Equal(0, torn);
    }

    [Fact]
    public async Task Two_optimistic_updates_on_one_key_roll_back_to_the_original_without_a_refetch()
    {
        using var client = CreateClient();
        var fetches = 0;
        await client.FetchAsync(A, _ => { fetches++; return Task.FromResult(10); });
        using var observer = client.Watch<int>(A);
        var mutation = client.CreateMutation<int, int>(
            (_, _) => throw new InvalidOperationException("rejected"),
            new MutationOptions<int, int>
            {
                ThrowOnError = false,
                OptimisticUpdates =
                [
                    OptimisticUpdate.Create<int, int>(A, (n, v) => n + v),
                    OptimisticUpdate.Create<int, int>(A, (n, _) => n * 2),
                ],
            });

        await mutation.MutateAsync(5);

        Assert.Equal(10, client.GetQueryData<int>(A).Data);
        Assert.Equal(1, fetches);
    }
}
