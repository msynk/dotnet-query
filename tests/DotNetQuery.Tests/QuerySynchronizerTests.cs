using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using DotNetQuery;
using DotNetQuery.Synchronization;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Tests;

public sealed class QuerySynchronizerTests : IDisposable
{
    private static readonly QueryKey Todos = QueryKey.Create("todos");
    private static readonly JsonTypeInfo<List<string>> ListInfo = (JsonTypeInfo<List<string>>)TestUtil.WebJson.GetTypeInfo(typeof(List<string>));

    private readonly FakeTimeProvider _time = new();
    private readonly InMemoryQuerySyncHub _hub = new();
    private readonly List<IDisposable> _disposables = [];
    private int _messages;

    private (QueryClient Client, QuerySynchronizer Sync) Tab(Action<QuerySynchronizer>? register = null, string channelName = "default")
    {
        var client = new QueryClient(TestUtil.NoRetry(o => o.DefaultStaleTime = TimeSpan.FromMinutes(5)), _time);
        var channel = _hub.CreateChannel();
        channel.MessageReceived += _ => Interlocked.Increment(ref _messages);
        var sync = new QuerySynchronizer(client, channel, new QuerySyncOptions { ChannelName = channelName });
        if (register is null)
        {
            sync.Register(Todos, ListInfo);
        }
        else
        {
            register(sync);
        }

        sync.Start();
        _disposables.Add(sync);
        _disposables.Add(client);
        return (client, sync);
    }

    public void Dispose() => _disposables.ForEach(d => d.Dispose());

    [Fact]
    public void Writes_reach_the_other_tab_with_their_timestamp_and_are_not_echoed()
    {
        var (a, _) = Tab();
        var (b, _) = Tab();

        a.SetQueryData(Todos, new List<string> { "milk" });

        var received = b.GetQueryData<List<string>>(Todos);
        Assert.Equal(["milk"], received.Data);
        Assert.Equal(a.GetQueryData<List<string>>(Todos).DataUpdatedAt, received.DataUpdatedAt);
        Assert.Equal(1, Volatile.Read(ref _messages)); // one message from A; B did not send it back
    }

    [Fact]
    public async Task Fetched_data_is_shared_and_fresh_in_the_other_tab()
    {
        var (a, _) = Tab();
        var (b, _) = Tab();
        var calls = 0;

        await a.FetchAsync(Todos, _ => { calls++; return Task.FromResult(new List<string> { "from server" }); });
        var inB = await b.FetchAsync(Todos, _ => { calls++; return Task.FromResult(new List<string> { "again" }); });

        Assert.Equal(["from server"], inB.Data);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Invalidation_refetches_the_observed_query_in_the_other_tab()
    {
        var (a, _) = Tab();
        var (b, _) = Tab();
        var bCalls = 0;
        using var observer = b.Watch<List<string>>(Todos);
        await b.FetchAsync(Todos, _ => Task.FromResult(new List<string> { $"b{++bCalls}" }));
        await a.FetchAsync(Todos, _ => Task.FromResult(new List<string> { "a" }));
        var before = bCalls;

        await a.InvalidateQueriesAsync(Todos);

        await TestUtil.WaitUntilAsync(() => bCalls > before);
    }

    [Fact]
    public void Concurrent_writes_converge_on_the_latest()
    {
        var (a, _) = Tab();
        var (b, _) = Tab();
        a.SetQueryData(Todos, new List<string> { "from a" });
        _time.Advance(TimeSpan.FromSeconds(1));
        b.SetQueryData(Todos, new List<string> { "from b" });

        Assert.Equal(["from b"], a.GetQueryData<List<string>>(Todos).Data);
        Assert.Equal(["from b"], b.GetQueryData<List<string>>(Todos).Data);
    }

    [Fact]
    public void Older_data_from_another_sender_is_ignored()
    {
        var (a, syncA) = Tab();
        var (b, _) = Tab();
        b.SetQueryData(Todos, new List<string> { "b" });
        _time.Advance(TimeSpan.FromSeconds(5));
        b.SetQueryData(Todos, new List<string> { "b newer" });

        // A hydrates an older value from elsewhere (for example a persisted cache) - it must not win in B.
        a.HydrateQueryData(Todos, new List<string> { "stale" }, _time.GetUtcNow() - TimeSpan.FromMinutes(1));

        Assert.Equal(["b newer"], b.GetQueryData<List<string>>(Todos).Data);
        Assert.NotNull(syncA);
    }

    [Fact]
    public async Task Optimistic_updates_and_their_rollback_propagate()
    {
        var (a, _) = Tab();
        var (b, _) = Tab();
        a.SetQueryData(Todos, new List<string> { "milk" });
        _time.Advance(TimeSpan.FromSeconds(1));
        var release = TestUtil.NewSignal();
        var add = a.CreateMutation<string, string>(
            async (_, ct) => { await release.Task.WaitAsync(ct); throw new InvalidOperationException("rejected"); },
            new MutationOptions<string, string>
            {
                ThrowOnError = false,
                OptimisticUpdates = [OptimisticUpdate.Create<string, List<string>>(Todos, (list, title) => [.. list ?? [], title])],
            });

        var run = add.MutateAsync("eggs");
        await TestUtil.WaitUntilAsync(() => b.GetQueryData<List<string>>(Todos).Data!.Count == 2);

        release.SetResult();
        await run;

        Assert.Equal(["milk"], a.GetQueryData<List<string>>(Todos).Data);
        Assert.Equal(["milk"], b.GetQueryData<List<string>>(Todos).Data);
    }

    [Fact]
    public void Different_channel_names_do_not_mix()
    {
        var (a, _) = Tab(channelName: "v1");
        var (b, _) = Tab(channelName: "v2");

        a.SetQueryData(Todos, new List<string> { "v1 data" });

        Assert.False(b.GetQueryData<List<string>>(Todos).HasData);
    }

    [Fact]
    public void Prefix_registrations_update_only_queries_the_receiver_holds()
    {
        void Register(QuerySynchronizer sync) => sync.RegisterPrefix(QueryKey.Create("lists"), ListInfo);
        var (a, _) = Tab(Register);
        var (b, _) = Tab(Register);
        var shared = QueryKey.Create("lists", 1);
        var onlyInA = QueryKey.Create("lists", 2);
        b.SetQueryData(shared, new List<string> { "old" });
        _time.Advance(TimeSpan.FromSeconds(1));

        a.SetQueryData(shared, new List<string> { "new" });
        a.SetQueryData(onlyInA, new List<string> { "x" });

        Assert.Equal(["new"], b.GetQueryData<List<string>>(shared).Data);
        Assert.False(b.Cache.TryGetState<List<string>>(onlyInA, out _));
    }

    [Fact]
    public async Task Malformed_messages_are_reported_and_skipped()
    {
        var hub = new InMemoryQuerySyncHub();
        var raw = hub.CreateChannel();
        using var client = new QueryClient(TestUtil.NoRetry(), _time);
        using var sync = new QuerySynchronizer(client, hub.CreateChannel()).Register(Todos, ListInfo).Start();
        var errors = new List<Exception>();
        sync.Error += errors.Add;

        await raw.PostAsync("{ not json");
        await raw.PostAsync("""{"channel":"default","sender":"x","kind":"Data","key":"[\"todos\"]","payload":"not a list","updatedAt":"2026-01-01T00:00:00Z"}""");

        Assert.Equal(2, errors.Count);
        Assert.False(client.GetQueryData<List<string>>(Todos).HasData);
    }

    [Fact]
    public void Disposed_synchronizers_stop_sending_and_receiving()
    {
        var (a, syncA) = Tab();
        var (b, _) = Tab();
        syncA.Dispose();

        a.SetQueryData(Todos, new List<string> { "local only" });

        Assert.False(b.GetQueryData<List<string>>(Todos).HasData);
        Assert.Equal(0, Volatile.Read(ref _messages));
    }
}
