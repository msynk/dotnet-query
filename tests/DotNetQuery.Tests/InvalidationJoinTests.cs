using DotNetQuery;
using Xunit;

namespace DotNetQuery.Tests;

/// <summary>
/// A mutation's invalidation joins a refresh that started after its server call settled (that refresh already reflects
/// the write) instead of cancelling and restarting it; anything that may predate the write is still restarted.
/// </summary>
public class InvalidationJoinTests
{
    private static readonly QueryKey Todos = QueryKey.Create("todos");

    private sealed class Counter
    {
        public int Started;
        public int Cancelled;

        public void Attach(QueryClient client) => client.DiagnosticEvent += e =>
        {
            switch (e)
            {
                case QueryStartedEvent s when s.Key == Todos:
                    Interlocked.Increment(ref Started);
                    break;
                case QueryCancelledEvent c when c.Key == Todos:
                    Interlocked.Increment(ref Cancelled);
                    break;
            }
        };
    }

    [Fact]
    public async Task Mutations_settling_at_the_same_time_refetch_once_without_cancelling()
    {
        for (var round = 0; round < 50; round++)
        {
            using var client = new QueryClient(TestUtil.NoRetry(o => o.DefaultStaleTime = TimeSpan.FromMinutes(5)));
            var counter = new Counter();
            using var observer = client.Watch<int>(Todos);
            await client.FetchAsync(Todos, async _ => { await Task.Yield(); return 0; });
            counter.Attach(client);

            // Both server calls have answered before either mutation invalidates (the responses arrived together); the
            // two invalidations then race.
            var answered = 0;
            var bothAnswered = TestUtil.NewSignal();
            var options = new MutationOptions<int, int>
            {
                Invalidates = [Todos],
                OnSuccess = async (_, _, _, _) =>
                {
                    if (Interlocked.Increment(ref answered) == 2)
                    {
                        bothAnswered.SetResult();
                    }

                    await bothAnswered.Task;
                },
            };
            var a = client.CreateMutation<int, int>((v, _) => Task.FromResult(v), options).MutateAsync(1);
            var b = client.CreateMutation<int, int>((v, _) => Task.FromResult(v), options).MutateAsync(2);
            await Task.WhenAll(a, b);
            await TestUtil.WaitUntilAsync(() => client.Cache.Describe(Todos)!.Status == QueryStatus.Success);

            Assert.Equal(1, Volatile.Read(ref counter.Started));
            Assert.Equal(0, Volatile.Read(ref counter.Cancelled));
            Assert.False(client.Cache.Describe(Todos)!.IsStale);
        }
    }

    [Fact]
    public async Task A_refresh_that_started_before_the_server_call_settled_is_still_restarted()
    {
        using var client = new QueryClient(TestUtil.NoRetry(o => o.DefaultStaleTime = TimeSpan.FromMinutes(5)));
        var counter = new Counter();
        using var observer = client.Watch<int>(Todos);
        var version = 0;
        await client.FetchAsync(Todos, _ => Task.FromResult(version));
        counter.Attach(client);

        // A refetch is in flight (it reads the server before the write)...
        var slowStarted = TestUtil.NewSignal();
        var slowRelease = TestUtil.NewSignal();
        var calls = 0;
        var slow = client.FetchAsync(Todos, async ct =>
        {
            var seen = version;
            if (Interlocked.Increment(ref calls) == 1)
            {
                slowStarted.SetResult();
                await slowRelease.Task.WaitAsync(ct);
            }

            return seen;
        }, forceRefetch: true);
        await slowStarted.Task;

        // ...when the write settles: the in-flight result is outdated, so the invalidation replaces it.
        var mutation = client.CreateMutation<int, int>((v, _) => { version = v; return Task.FromResult(v); }, new() { Invalidates = [Todos] });
        await mutation.MutateAsync(7);
        slowRelease.SetResult();
        await slow;
        await TestUtil.WaitUntilAsync(() => client.Cache.Describe(Todos)!.Status == QueryStatus.Success);

        Assert.Equal(2, counter.Started);
        Assert.Equal(1, counter.Cancelled);
        Assert.Equal(7, client.GetQueryData<int>(Todos).Data);
    }

    [Fact]
    public async Task Plain_invalidation_still_restarts_an_in_flight_fetch()
    {
        using var client = new QueryClient(TestUtil.NoRetry(o => o.DefaultStaleTime = TimeSpan.FromMinutes(5)));
        var counter = new Counter();
        using var observer = client.Watch<int>(Todos);
        await client.FetchAsync(Todos, _ => Task.FromResult(0));
        counter.Attach(client);

        var release = TestUtil.NewSignal();
        var inFlight = client.FetchAsync(Todos, async _ => { await release.Task; return 1; }, forceRefetch: true);
        await TestUtil.WaitUntilAsync(() => counter.Started == 1);
        var invalidation = client.InvalidateQueriesAsync(Todos);   // the change time is unknown: always restart
        release.SetResult();
        await Task.WhenAll(inFlight, invalidation);

        Assert.Equal(2, counter.Started);
        Assert.Equal(1, counter.Cancelled);
    }

    [Fact]
    public async Task A_refresh_that_completed_after_the_server_call_covers_the_invalidation()
    {
        using var client = new QueryClient(TestUtil.NoRetry(o => o.DefaultStaleTime = TimeSpan.FromMinutes(5)));
        var counter = new Counter();
        using var observer = client.Watch<int>(Todos);
        await client.FetchAsync(Todos, _ => Task.FromResult(0));
        counter.Attach(client);

        var mutation = client.CreateMutation<int, int>((v, _) => Task.FromResult(v), new MutationOptions<int, int>
        {
            Invalidates = [Todos],
            // Runs after the server answered and before the invalidation: a refresh that already reflects the write.
            OnSettled = async (_, _, _, _, _) => await client.RefetchQueryAsync(Todos),
        });
        await mutation.MutateAsync(1);

        Assert.Equal(1, counter.Started);
        Assert.Equal(0, counter.Cancelled);
        Assert.False(client.Cache.Describe(Todos)!.IsStale);
    }

    [Fact]
    public async Task An_infinite_querys_next_page_fetch_does_not_cover_an_invalidation()
    {
        using var client = new QueryClient(TestUtil.NoRetry(o => o.DefaultStaleTime = TimeSpan.FromMinutes(5)));
        var firstPages = 0;
        var nextPageRelease = TestUtil.NewSignal();
        using var feed = client.CreateInfiniteQuery<int, int>(Todos, async (page, ct) =>
        {
            if (page == 0)
            {
                Interlocked.Increment(ref firstPages);
            }
            else
            {
                await nextPageRelease.Task.WaitAsync(ct);
            }

            return page;
        }, new InfiniteQueryOptions<int, int> { InitialPageParam = 0, GetNextPageParam = (last, _, _) => NextPageParam.Of(last + 1) });
        await feed.FetchAsync();

        Task? nextPage = null;
        var mutation = client.CreateMutation<int, int>((v, _) => Task.FromResult(v), new MutationOptions<int, int>
        {
            Invalidates = [Todos],
            // Starts after the server answered, but only adds a page: the invalidation must still refresh the pages.
            OnSettled = (_, _, _, _, _) => { nextPage = feed.FetchNextPageAsync(); return ValueTask.CompletedTask; },
        });
        await mutation.MutateAsync(1);
        nextPageRelease.SetResult();
        await nextPage!;

        Assert.Equal(2, firstPages);
    }
}
