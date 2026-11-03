using DotNetQuery;
using Xunit;

namespace DotNetQuery.Tests;

public class OptimisticLayerTests
{
    private static readonly QueryKey Todos = QueryKey.Create("todos");

    private sealed class Harness : IDisposable
    {
        private readonly Dictionary<string, TaskCompletionSource<string>> _servers = [];

        public Harness()
        {
            Client = new QueryClient(TestUtil.NoRetry(o => o.DefaultStaleTime = TimeSpan.FromMinutes(5)));
        }

        public QueryClient Client { get; }

        public int Fetches { get; private set; }

        public List<string> Server { get; } = ["base"];

        public async Task LoadAsync()
        {
            await Client.FetchAsync(Todos, _ => { Fetches++; return Task.FromResult(Server.ToList()); });
        }

        // Starts adding `title` optimistically; the server answers when Accept/Reject is called.
        public Task<MutationResult<string, string>> Add(string title, Func<List<string>?, string, List<string>>? update = null)
        {
            var server = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _servers[title] = server;
            var mutation = Client.CreateMutation<string, string>(
                (t, _) => server.Task,
                new MutationOptions<string, string>
                {
                    ThrowOnError = false,
                    OptimisticUpdates = [OptimisticUpdate.Create(Todos, update ?? ((list, t) => [.. list ?? [], t]))],
                });
            return mutation.MutateAsync(title);
        }

        public void Accept(string title) => _servers[title].SetResult(title);

        public void Reject(string title) => _servers[title].SetException(new InvalidOperationException($"{title} rejected"));

        public List<string> Data => Client.GetQueryData<List<string>>(Todos).Data!;

        public void Dispose() => Client.Dispose();
    }

    [Fact]
    public async Task A_failed_mutation_below_another_pending_one_keeps_the_other_optimistic_value()
    {
        using var h = new Harness();
        await h.LoadAsync();
        var a = h.Add("a");
        var b = h.Add("b");
        await TestUtil.WaitUntilAsync(() => h.Data.Count == 3);

        h.Reject("a");
        await a;

        Assert.Equal(["base", "b"], h.Data); // b survives without a refetch
        Assert.Equal(1, h.Fetches);

        h.Accept("b");
        await b;
        Assert.Equal(["base", "b"], h.Data);
    }

    [Fact]
    public async Task Layers_roll_back_in_any_order_to_exactly_the_right_state()
    {
        using var h = new Harness();
        await h.LoadAsync();
        var original = h.Client.GetQueryData<List<string>>(Todos).State;
        var a = h.Add("a");
        var b = h.Add("b");
        var c = h.Add("c");
        await TestUtil.WaitUntilAsync(() => h.Data.Count == 4);

        h.Reject("b");
        await b;
        Assert.Equal(["base", "a", "c"], h.Data);

        h.Reject("c");
        await c;
        Assert.Equal(["base", "a"], h.Data);

        h.Reject("a");
        await a;
        Assert.Equal(original, h.Client.GetQueryData<List<string>>(Todos).State); // exact snapshot, timestamp included
        Assert.Equal(1, h.Fetches);
    }

    [Fact]
    public async Task An_accepted_mutation_above_a_failed_one_is_re_applied()
    {
        using var h = new Harness();
        await h.LoadAsync();
        var a = h.Add("a");
        var b = h.Add("b");
        await TestUtil.WaitUntilAsync(() => h.Data.Count == 3);

        h.Accept("b");
        await b;
        h.Reject("a");
        await a;

        Assert.Equal(["base", "b"], h.Data);
        Assert.Equal(1, h.Fetches);
    }

    [Fact]
    public async Task A_foreign_write_in_between_falls_back_to_a_refetch()
    {
        using var h = new Harness();
        using var observer = h.Client.Watch<List<string>>(Todos);
        await h.LoadAsync();
        var a = h.Add("a");
        var b = h.Add("b");
        await TestUtil.WaitUntilAsync(() => h.Data.Count == 3);
        h.Client.SetQueryData(Todos, new List<string> { "server truth" });

        h.Reject("a");
        await a;

        // The stack no longer describes the data: reconcile with the server instead of guessing.
        await TestUtil.WaitUntilAsync(() => h.Fetches == 2);
        h.Accept("b");
        await b;
    }

    [Fact]
    public async Task A_throwing_updater_during_a_rebase_falls_back_to_a_refetch()
    {
        using var h = new Harness();
        using var observer = h.Client.Watch<List<string>>(Todos);
        await h.LoadAsync();
        var applied = 0;
        var a = h.Add("a");
        var b = h.Add("b", (list, t) => Interlocked.Increment(ref applied) > 1 ? throw new InvalidOperationException("not re-appliable") : [.. list ?? [], t]);
        await TestUtil.WaitUntilAsync(() => h.Data.Count == 3);

        h.Reject("a");
        await a;

        await TestUtil.WaitUntilAsync(() => h.Fetches == 2);
        h.Accept("b");
        await b;
    }

    [Fact]
    public async Task Many_overlapping_mutations_settling_in_random_order_converge()
    {
        using var h = new Harness();
        await h.LoadAsync();
        var titles = Enumerable.Range(0, 30).Select(i => $"t{i}").ToList();
        var runs = titles.ToDictionary(t => t, t => h.Add(t));
        await TestUtil.WaitUntilAsync(() => h.Data.Count == 31);
        var random = new Random(42);
        var accepted = new HashSet<string>();

        foreach (var title in titles.OrderBy(_ => random.Next()))
        {
            if (random.Next(2) == 0)
            {
                h.Accept(title);
                accepted.Add(title);
            }
            else
            {
                h.Reject(title);
            }

            await runs[title];
        }

        Assert.Equal(["base", .. titles.Where(accepted.Contains)], h.Data);
        Assert.Equal(1, h.Fetches);
    }
}
