using DotNetQuery;
using Xunit;

namespace DotNetQuery.Tests;

/// <summary>
/// While optimistic writes of pending mutations are on screen, refreshes of the query wait for them and run once they
/// settle, so a refetch never replaces pending optimistic values with server data that does not include them yet.
/// </summary>
public class OptimisticRefetchDeferralTests
{
    private static readonly QueryKey Cart = QueryKey.Create("cart");

    private sealed class Store
    {
        private readonly Dictionary<string, TaskCompletionSource<bool>> _pending = [];

        public List<string> Items { get; } = [];

        public int Fetches;

        public Task<List<string>> GetAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Fetches);
            lock (Items)
            {
                return Task.FromResult(Items.ToList());
            }
        }

        public Task<string> AddAsync(string item, CancellationToken cancellationToken)
        {
            var answer = TestUtil.NewSignal<bool>();
            lock (_pending)
            {
                _pending[item] = answer;
            }

            return Wait();

            async Task<string> Wait()
            {
                if (!await answer.Task)
                {
                    throw new InvalidOperationException($"{item} is out of stock");
                }

                lock (Items)
                {
                    Items.Add(item);
                }

                return item;
            }
        }

        public void Answer(string item, bool accept)
        {
            lock (_pending)
            {
                _pending[item].SetResult(accept);
            }
        }

        public async Task WaitForRequestAsync(string item) => await TestUtil.WaitUntilAsync(() => { lock (_pending) { return _pending.ContainsKey(item); } });
    }

    private static (QueryClient Client, Store Store, Mutation<string, string> Add, List<List<string>> Seen) Setup(bool invalidates = true)
    {
        var client = new QueryClient(TestUtil.NoRetry(o => o.DefaultStaleTime = TimeSpan.FromMinutes(5)));
        var store = new Store();
        var seen = new List<List<string>>();
        var add = client.CreateMutation<string, string>(store.AddAsync, new MutationOptions<string, string>
        {
            ThrowOnError = false,
            OptimisticUpdates = [OptimisticUpdate.Create<string, List<string>>(Cart, (cart, item) => [.. cart ?? [], item])],
            Invalidates = invalidates ? [Cart] : null,
        });
        return (client, store, add, seen);
    }

    private static List<string> Data(QueryClient client) => client.GetQueryData<List<string>>(Cart).Data ?? [];

    [Fact]
    public async Task A_settled_mutations_invalidation_waits_for_the_other_pending_ones_and_refetches_once()
    {
        var (client, store, add, seen) = Setup();
        using var _ = client;
        using var observer = client.Watch<List<string>>(Cart);
        observer.StateChanged += state => { lock (seen) { seen.Add(state.Data?.ToList() ?? []); } };
        await client.FetchAsync(Cart, store.GetAsync);
        var fetchesBefore = store.Fetches;

        var keyboard = add.MutateAsync("Keyboard");
        var mouse = add.MutateAsync("Mouse");
        var monitor = add.MutateAsync("Monitor");
        await Task.WhenAll(store.WaitForRequestAsync("Keyboard"), store.WaitForRequestAsync("Mouse"), store.WaitForRequestAsync("Monitor"));
        Assert.Equal(["Keyboard", "Mouse", "Monitor"], Data(client));

        // The rejected add settles first: its rollback keeps the others, and its invalidation must not refetch the
        // server's (still empty) cart over them.
        store.Answer("Mouse", accept: false);
        await mouse;
        Assert.Equal(["Keyboard", "Monitor"], Data(client));
        Assert.Equal(fetchesBefore, store.Fetches);

        store.Answer("Keyboard", accept: true);
        await keyboard;
        Assert.Equal(["Keyboard", "Monitor"], Data(client));
        Assert.Equal(fetchesBefore, store.Fetches);

        store.Answer("Monitor", accept: true);
        await monitor;
        await TestUtil.WaitUntilAsync(() => !client.Cache.Describe(Cart)!.Status.Equals(QueryStatus.Refetching) && store.Fetches > fetchesBefore);
        await Task.Delay(50);

        Assert.Equal(fetchesBefore + 1, store.Fetches);   // one reconciliation for the whole burst
        Assert.Equal(["Keyboard", "Monitor"], Data(client));
        lock (seen)
        {
            // Every state shown after the burst started contains the items still pending or accepted at that time:
            // never the server's cart without them.
            var afterBurst = seen.SkipWhile(s => s.Count == 0).ToList();
            Assert.DoesNotContain(afterBurst, s => s.Count == 0);
            var lastWithMouse = afterBurst.FindLastIndex(s => s.Contains("Mouse"));
            Assert.All(afterBurst.Skip(lastWithMouse + 1), s => Assert.Equal(["Keyboard", "Monitor"], s));
        }
    }

    [Fact]
    public async Task Refreshes_requested_while_optimistic_writes_are_pending_run_once_after_they_settle()
    {
        var (client, store, add, _) = Setup(invalidates: false);
        using var __ = client;
        using var observer = client.Watch<List<string>>(Cart);
        await client.FetchAsync(Cart, store.GetAsync);
        var fetchesBefore = store.Fetches;

        var adding = add.MutateAsync("Keyboard");
        await store.WaitForRequestAsync("Keyboard");

        await client.InvalidateQueriesAsync(Cart);          // completes at once: the refetch is deferred
        await client.RefetchQueryAsync(Cart);
        await client.FetchAsync(Cart, store.GetAsync);       // a stale read (invalidated) is deferred too
        Assert.Equal(fetchesBefore, store.Fetches);
        Assert.Equal(["Keyboard"], Data(client));
        Assert.True(client.Cache.Describe(Cart)!.IsStale);

        store.Answer("Keyboard", accept: true);
        await adding;
        await TestUtil.WaitUntilAsync(() => store.Fetches == fetchesBefore + 1);
        await TestUtil.WaitUntilAsync(() => client.Cache.Describe(Cart)!.Status == QueryStatus.Success);
        await Task.Delay(50);

        Assert.Equal(fetchesBefore + 1, store.Fetches);
        Assert.Equal(["Keyboard"], Data(client));
        Assert.False(client.Cache.Describe(Cart)!.IsStale);
    }

    [Fact]
    public async Task An_inactive_query_whose_refresh_was_deferred_stays_stale_for_its_next_use()
    {
        var (client, store, add, _) = Setup(invalidates: false);
        using var __ = client;
        await client.FetchAsync(Cart, store.GetAsync);
        var fetchesBefore = store.Fetches;

        var adding = add.MutateAsync("Keyboard");
        await store.WaitForRequestAsync("Keyboard");
        await client.RefetchQueryAsync(Cart);
        store.Answer("Keyboard", accept: true);
        await adding;
        await Task.Delay(50);

        Assert.Equal(fetchesBefore, store.Fetches);     // nobody shows it: nothing refetched
        Assert.True(client.Cache.Describe(Cart)!.IsStale);
        await client.FetchAsync(Cart, store.GetAsync);  // the next use refetches
        Assert.Equal(fetchesBefore + 1, store.Fetches);
    }

    [Fact]
    public async Task Scoped_optimistic_mutations_with_invalidation_do_not_deadlock()
    {
        using var client = new QueryClient(TestUtil.NoRetry());
        var store = new Store();
        using var observer = client.Watch<List<string>>(Cart);
        await client.FetchAsync(Cart, store.GetAsync);
        var add = client.CreateMutation<string, string>(store.AddAsync, new MutationOptions<string, string>
        {
            Scope = "cart",
            ThrowOnError = false,
            OptimisticUpdates = [OptimisticUpdate.Create<string, List<string>>(Cart, (cart, item) => [.. cart ?? [], item])],
            Invalidates = [Cart],
        });

        var first = add.MutateAsync("a");
        var second = add.MutateAsync("b");   // its optimistic write is pending while it waits for the scope
        await store.WaitForRequestAsync("a");
        store.Answer("a", accept: true);
        await first.WaitAsync(TimeSpan.FromSeconds(5));   // a's invalidation is deferred, not awaited behind b
        await store.WaitForRequestAsync("b");
        store.Answer("b", accept: true);
        await second.WaitAsync(TimeSpan.FromSeconds(5));

        await TestUtil.WaitUntilAsync(() => client.Cache.Describe(Cart)!.Status == QueryStatus.Success && Data(client).Count == 2);
        Assert.Equal(["a", "b"], Data(client));
    }

    [Fact]
    public async Task Infinite_next_page_fetches_are_not_deferred()
    {
        using var client = new QueryClient(TestUtil.NoRetry());
        var key = QueryKey.Create("feed");
        using var feed = client.CreateInfiniteQuery<int, int[]>(key, (page, _) => Task.FromResult(new[] { page }), new InfiniteQueryOptions<int, int[]>
        {
            InitialPageParam = 0,
            GetNextPageParam = (last, _, _) => NextPageParam.Of(last[0] + 1),
        });
        await feed.FetchAsync();
        var release = TestUtil.NewSignal<bool>();
        var post = client.CreateMutation<int, bool>((_, _) => release.Task, new MutationOptions<int, bool>
        {
            OptimisticUpdates = [OptimisticUpdate.Create<int, InfiniteData<int, int[]>>(key, (data, value) => data! with { Pages = [[value, .. data.Pages[0]], .. data.Pages.Skip(1)] })],
        });

        var posting = post.MutateAsync(-1);
        await TestUtil.WaitUntilAsync(() => feed.CurrentState.Data?.Pages[0][0] == -1);
        await feed.FetchNextPageAsync();

        Assert.Equal(2, feed.CurrentState.PageCount);
        release.SetResult(true);
        await posting;
    }
}
