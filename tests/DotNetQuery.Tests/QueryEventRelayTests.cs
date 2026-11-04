using DotNetQuery;
using DotNetQuery.Synchronization;
using Xunit;

namespace DotNetQuery.Tests;

public sealed class QueryEventRelayTests : IAsyncLifetime
{
    private sealed record Todo(int Id, string Title);

    private static readonly QueryKey Todos = QueryKey.Create("todos");
    private static readonly QueryKey Other = QueryKey.Create("other");

    private readonly InMemoryQuerySyncHub _hub = new();
    private readonly List<IAsyncDisposable> _disposables = [];
    private readonly List<IDisposable> _others = [];
    private int _messages;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _others.ForEach(d => d.Dispose());
        foreach (var disposable in _disposables)
        {
            await disposable.DisposeAsync();
        }
    }

    private (QueryClient Client, QueryEventSynchronizer Sync, QueryEventRelay Relay, IQuerySyncChannel Channel) Tab(string? scope = null, string channelName = "v1")
    {
        var client = new QueryClient(TestUtil.NoRetry(o => o.DefaultStaleTime = TimeSpan.FromMinutes(5)));
        var sync = new QueryEventSynchronizer(client, options: new QueryEventSyncOptions { Scope = scope });
        sync.Map<Todo>("TodoChanged", m => m
            .WithMetadata(t => new QueryEventMetadata { EventId = $"evt-{t.Id}-{t.Title}" })
            .Invalidate(Todos));
        var channel = _hub.CreateChannel();
        channel.MessageReceived += _ => Interlocked.Increment(ref _messages);
        var relay = new QueryEventRelay(sync, channel, new QueryEventRelayOptions { ChannelName = channelName }).Start();
        _others.Add(relay);
        _disposables.Add(sync);
        _others.Add(client);
        return (client, sync, relay, channel);
    }

    private static async Task SeedAsync(QueryClient client)
    {
        await client.FetchAsync(Todos.Append("list"), _ => Task.FromResult(1));
        await client.FetchAsync(Other, _ => Task.FromResult(1));
    }

    private static bool IsStale(QueryClient client, QueryKey key) => client.Cache.Describe(key)!.IsStale;

    [Fact]
    public async Task A_processed_event_invalidates_what_the_other_tabs_mappings_cover_without_bouncing_back()
    {
        var (leader, leaderSync, leaderRelay, _) = Tab();
        var (follower, _, followerRelay, _) = Tab();
        await SeedAsync(follower);

        await leaderSync.ProcessAsync("TodoChanged", new Todo(1, "a"));
        await TestUtil.WaitUntilAsync(() => IsStale(follower, Todos.Append("list")));

        Assert.False(IsStale(follower, Other)); // only what the follower's own mappings cover
        Assert.Equal(1, leaderRelay.MessagesSent);
        Assert.Equal(1, followerRelay.MessagesApplied);
        Assert.Equal(0, followerRelay.MessagesSent); // received notices are never relayed again
        Assert.Equal(1, Volatile.Read(ref _messages));
        leader.Dispose();
    }

    [Fact]
    public async Task A_tab_that_already_processed_the_event_skips_the_notice()
    {
        var (_, leaderSync, _, _) = Tab();
        var (follower, followerSync, _, _) = Tab();
        await followerSync.ProcessAsync("TodoChanged", new Todo(1, "a")); // its own connection delivered it first
        await SeedAsync(follower);

        await leaderSync.ProcessAsync("TodoChanged", new Todo(1, "a"));
        Assert.Equal(QueryEventOutcome.NoMapping, await followerSync.ProcessAsync("Barrier", 0)); // queued after the notice

        Assert.False(IsStale(follower, Todos.Append("list")));
    }

    [Fact]
    public async Task Notices_from_another_scope_or_build_are_ignored()
    {
        var (_, tenantA, _, _) = Tab(scope: "tenant-a");
        var (followerB, _, relayB, _) = Tab(scope: "tenant-b");
        var (followerOld, _, relayOld, _) = Tab(scope: "tenant-a", channelName: "v0");
        await SeedAsync(followerB);
        await SeedAsync(followerOld);

        await tenantA.ProcessAsync("TodoChanged", new Todo(1, "a"));
        await TestUtil.WaitUntilAsync(() => relayB.MessagesIgnored == 1 && relayOld.MessagesIgnored == 1);

        Assert.False(IsStale(followerB, Todos.Append("list")));
        Assert.False(IsStale(followerOld, Todos.Append("list")));
    }

    [Fact]
    public async Task A_recovery_tells_the_other_tabs_to_reconcile_everything_covered()
    {
        var (_, leaderSync, _, _) = Tab();
        var (follower, _, followerRelay, _) = Tab();
        await SeedAsync(follower);

        await leaderSync.ResynchronizeAsync();
        await TestUtil.WaitUntilAsync(() => IsStale(follower, Todos.Append("list")));
        Assert.Equal(1, followerRelay.MessagesApplied);
    }

    [Fact]
    public async Task Malformed_duplicate_and_own_messages_are_ignored()
    {
        var (_, sync, relay, channel) = Tab();
        var errors = 0;
        relay.Error += _ => Interlocked.Increment(ref errors);
        var injector = _hub.CreateChannel();
        await SeedAsync((QueryClient)sync.Client);

        await injector.PostAsync("{not json");
        await injector.PostAsync("""{"v":2,"id":"x","o":"other","op":1,"ev":"TodoChanged","ch":"v1"}""");     // unknown schema
        await injector.PostAsync("""{"v":1,"id":"x","o":"other","op":7,"ch":"v1"}""");                         // unknown operation
        await injector.PostAsync("""{"v":1,"id":"x","o":"other","op":1,"ch":"v1"}""");                         // no event name
        await injector.PostAsync("""{"v":1,"id":"m1","o":"other","op":1,"ev":"Unmapped","ch":"v1"}""");       // unknown event: no-op
        await injector.PostAsync("""{"v":1,"id":"m2","o":"other","op":1,"ev":"TodoChanged","ch":"v1"}""");
        await injector.PostAsync("""{"v":1,"id":"m2","o":"other","op":1,"ev":"TodoChanged","ch":"v1"}""");    // duplicate

        Assert.Equal(1, errors);
        Assert.Equal(2, relay.MessagesApplied);
        Assert.Equal(5, relay.MessagesIgnored);
        await TestUtil.WaitUntilAsync(() => IsStale((QueryClient)sync.Client, Todos.Append("list")));
        Assert.False(IsStale((QueryClient)sync.Client, Other));
        _ = channel;
    }

    [Fact]
    public void A_synchronizer_has_one_relay_and_disposal_detaches_it()
    {
        var (_, sync, relay, _) = Tab();
        var second = new QueryEventRelay(sync, _hub.CreateChannel());
        Assert.Throws<InvalidOperationException>(() => second.Start());

        relay.Dispose();
        new QueryEventRelay(sync, _hub.CreateChannel()).Start().Dispose();
    }
}
