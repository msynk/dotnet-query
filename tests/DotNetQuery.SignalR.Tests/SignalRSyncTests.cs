using DotNetQuery.Synchronization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DotNetQuery.SignalR.Tests;

public sealed class SignalRSyncTests : IAsyncLifetime
{
    private static readonly QueryKey Todos = QueryKey.Create("todos");
    private static readonly QueryKey TodoList = Todos.Append("list");
    private static readonly QueryEntity<Todo, int> TodoEntity = QueryEntity.Create<Todo, int>(Todos, t => t.Id, version: t => t.Version);

    private readonly List<IAsyncDisposable> _disposables = [];
    private HubServer _server = default!;

    public async Task InitializeAsync() => _server = await HubServer.StartAsync();

    public async Task DisposeAsync()
    {
        for (var i = _disposables.Count - 1; i >= 0; i--)
        {
            await _disposables[i].DisposeAsync();
        }

        await _server.DisposeAsync();
    }

    private static QueryClient NewClient() => new(new QueryClientOptions
    {
        DefaultRetryCount = 0,
        DefaultStaleTime = TimeSpan.FromMinutes(5),
        GarbageCollectionInterval = Timeout.InfiniteTimeSpan,
    });

    private (QueryClient Client, QueryEventSynchronizer Sync, HubConnectionQueryEventSource Source, HubConnection Connection) Setup(
        HubConnectionQueryEventSourceOptions? sourceOptions = null,
        QueryEventSyncOptions? syncOptions = null,
        bool automaticReconnect = true)
    {
        var client = NewClient();
        var connection = _server.CreateConnection(automaticReconnect);
        var source = new HubConnectionQueryEventSource(connection, sourceOptions);
        var sync = new QueryEventSynchronizer(client, source, syncOptions);
        _disposables.Add(source);
        _disposables.Add(sync);
        return (client, sync, source, connection);
    }

    private static void MapTodos(QueryEventSynchronizer sync) =>
        sync.Map<QueryEventEnvelope<Todo>>("TodoChanged", m => m.Upsert(TodoEntity, e => e.Payload, EntityMissingBehavior.Append));

    [Fact]
    public async Task Typed_hub_events_update_the_cache()
    {
        var (client, sync, _, connection) = Setup();
        client.SetQueryData(TodoList, new List<Todo> { new(1, "a") });
        MapTodos(sync);

        await sync.StartAsync();
        Assert.Equal(HubConnectionState.Connected, connection.State);
        await Wait.UntilAsync(() => sync.Status == QuerySyncStatus.Synchronized);

        await _server.PublishAsync(new Todo(1, "A", 2));
        await _server.PublishAsync(new Todo(2, "b"));
        await Wait.UntilAsync(() => client.GetQueryData<List<Todo>>(TodoList).Data!.Count == 2);

        Assert.Equal(["A", "b"], client.GetQueryData<List<Todo>>(TodoList).Data!.Select(t => t.Title));
        Assert.Equal(2, sync.GetStatistics().Applied);
    }

    [Fact]
    public async Task Redelivered_envelopes_are_discarded_by_id_and_sequence()
    {
        var (client, sync, _, _) = Setup();
        client.SetQueryData(Todos.Append("count"), 0);
        sync.Map<QueryEventEnvelope<Todo>>("TodoChanged", m => m.Update<int>(Todos.Append("count"), (n, _) => n + 1));
        await sync.StartAsync();

        var envelope = await _server.PublishAsync(new Todo(1, "a"));
        await _server.Hub.Clients.All.SendAsync("TodoChanged", envelope); // the same event again
        await _server.PublishAsync(new Todo(2, "b"));
        await Wait.UntilAsync(() => sync.GetStatistics() is { Applied: 2, Duplicates: 1 });

        Assert.Equal(2, client.GetQueryData<int>(Todos.Append("count")).Data);
    }

    [Fact]
    public async Task Reconnecting_rejoins_groups_and_recovers_missed_events()
    {
        var group = Guid.NewGuid().ToString("N");
        var server = new List<Todo> { new(1, "a") };
        var (client, sync, source, connection) = Setup(new HubConnectionQueryEventSourceOptions
        {
            OnConnected = (hub, ct) => hub.InvokeAsync("Join", group, ct),
        });
        MapTodos(sync);
        using var observer = client.Watch<List<Todo>>(TodoList);
        await client.FetchAsync(TodoList, _ => Task.FromResult(server.ToList()));
        var statuses = new List<QuerySyncStatus>();
        sync.StatusChanged += change => { lock (statuses) { statuses.Add(change.Status); } };

        await sync.StartAsync();
        await Wait.UntilAsync(() => sync.Status == QuerySyncStatus.Synchronized);
        Assert.Equal(1, TodoHub.Joins[group]);

        // The network drops; a change published meanwhile is missed by the client.
        _server.Network.Break();
        await Wait.UntilAsync(() => sync.Status == QuerySyncStatus.Reconnecting);
        server.Add(new Todo(2, "missed"));
        await _server.PublishAsync(new Todo(2, "missed"), group);

        _server.Network.Restore();
        await Wait.UntilAsync(() => sync.Status == QuerySyncStatus.Synchronized && connection.State == HubConnectionState.Connected);
        await Wait.UntilAsync(() => client.GetQueryData<List<Todo>>(TodoList).Data!.Count == 2);

        Assert.Equal(2, TodoHub.Joins[group]); // the new connection rejoined its group
        lock (statuses)
        {
            Assert.Contains(QuerySyncStatus.Resubscribing, statuses);
            Assert.Contains(QuerySyncStatus.Synchronizing, statuses);
        }

        // Group events reach the new connection - once (handlers are not registered twice).
        await _server.PublishAsync(new Todo(3, "after"), group);
        await Wait.UntilAsync(() => client.GetQueryData<List<Todo>>(TodoList).Data!.Count == 3);
        await Task.Delay(100);
        Assert.Equal(3, client.GetQueryData<List<Todo>>(TodoList).Data!.Count);
        Assert.Equal(QueryEventSourceState.Connected, source.State);
    }

    [Fact]
    public async Task Replay_recovery_resumes_from_the_checkpoint_through_the_hub()
    {
        var group = Guid.NewGuid().ToString("N");
        HubConnection? hub = null;
        var (client, sync, _, connection) = Setup(
            new HubConnectionQueryEventSourceOptions { OnConnected = (h, ct) => h.InvokeAsync("Join", group, ct) },
            new QueryEventSyncOptions
            {
                ContiguousSequences = true,
                Recovery = QuerySyncRecovery.Replay(async (context, ct) =>
                {
                    var missed = await hub!.InvokeAsync<List<QueryEventEnvelope<Todo>>>("EventsSince", context.GetCheckpoint() ?? 0, ct);
                    foreach (var envelope in missed)
                    {
                        await context.ApplyAsync("TodoChanged", envelope, ct);
                    }

                    return true;
                }),
            });
        hub = connection;
        client.SetQueryData(TodoList, new List<Todo>());
        MapTodos(sync);
        await sync.StartAsync();
        await Wait.UntilAsync(() => sync.Status == QuerySyncStatus.Synchronized);

        await _server.PublishAsync(new Todo(1, "a"), group);
        await Wait.UntilAsync(() => sync.GetCheckpoint() == 1);

        _server.Network.Break();
        await Wait.UntilAsync(() => sync.Status == QuerySyncStatus.Reconnecting);
        await _server.PublishAsync(new Todo(2, "b"), group);
        await _server.PublishAsync(new Todo(3, "c"), group);
        _server.Network.Restore();

        await Wait.UntilAsync(() => client.GetQueryData<List<Todo>>(TodoList).Data!.Count == 3);
        await Wait.UntilAsync(() => sync.Status == QuerySyncStatus.Synchronized);

        Assert.Equal([1, 2, 3], client.GetQueryData<List<Todo>>(TodoList).Data!.Select(t => t.Id));
        Assert.Equal(3, sync.GetCheckpoint());
        Assert.Equal(0, sync.GetStatistics().RecoveryFallbacks);
    }

    [Fact]
    public async Task Repeated_reconnect_cycles_keep_one_subscription_and_recover_each_time()
    {
        var (client, sync, _, connection) = Setup(syncOptions: new QueryEventSyncOptions { RecoverOnConnect = false });
        client.SetQueryData(Todos.Append("count"), 0);
        sync.Map<QueryEventEnvelope<Todo>>("TodoChanged", m => m.Update<int>(Todos.Append("count"), (n, _) => n + 1));
        await sync.StartAsync();

        for (var cycle = 0; cycle < 3; cycle++)
        {
            _server.Network.Break();
            await Wait.UntilAsync(() => connection.State == HubConnectionState.Reconnecting);
            _server.Network.Restore();
            await Wait.UntilAsync(() => connection.State == HubConnectionState.Connected && sync.Status == QuerySyncStatus.Synchronized);
        }

        await _server.PublishAsync(new Todo(1, "a"));
        await Wait.UntilAsync(() => client.GetQueryData<int>(Todos.Append("count")).Data == 1);
        await Task.Delay(100);

        Assert.Equal(1, client.GetQueryData<int>(Todos.Append("count")).Data);
        Assert.Equal(3, sync.GetStatistics().Recoveries);
    }

    [Fact]
    public async Task A_failed_start_reports_disconnected_and_a_restart_policy_retries()
    {
        _server.Network.Break();
        var (_, sync, source, _) = Setup();
        MapTodos(sync);

        await Assert.ThrowsAsync<HttpRequestException>(() => sync.StartAsync());
        Assert.Equal(QuerySyncStatus.Disconnected, sync.Status);
        Assert.Contains(sync.GetStatistics().RecentErrors, e => e.Message == "network down");

        var attempts = 0;
        var (_, retrying, _, connection) = Setup(new HubConnectionQueryEventSourceOptions
        {
            RestartDelay = (attempt, _) =>
            {
                if (Interlocked.Increment(ref attempts) == 3)
                {
                    _server.Network.Restore();
                }

                return TimeSpan.FromMilliseconds(20);
            },
        });
        MapTodos(retrying);

        await retrying.StartAsync();

        Assert.Equal(HubConnectionState.Connected, connection.State);
        Assert.True(attempts >= 3);
        await Wait.UntilAsync(() => retrying.Status == QuerySyncStatus.Synchronized);
        Assert.Equal(QueryEventSourceState.Disconnected, source.State);
    }

    [Fact]
    public async Task A_rejected_resubscription_is_not_retried_when_the_policy_declines()
    {
        var (_, sync, source, _) = Setup(new HubConnectionQueryEventSourceOptions
        {
            OnConnected = (hub, ct) => hub.InvokeAsync("Join", "forbidden", ct),
            RestartDelay = (_, error) => error is HubException ? null : TimeSpan.FromMilliseconds(10),
        });
        MapTodos(sync);

        await Assert.ThrowsAsync<HubException>(() => sync.StartAsync());

        Assert.Equal(QueryEventSourceState.Disconnected, source.State);
        Assert.Equal(QuerySyncStatus.Disconnected, sync.Status);
    }

    [Fact]
    public async Task A_connection_closed_for_good_restarts_with_a_policy()
    {
        var (client, sync, _, connection) = Setup(
            new HubConnectionQueryEventSourceOptions { RestartDelay = (_, _) => TimeSpan.FromMilliseconds(30) },
            automaticReconnect: false);
        MapTodos(sync);
        client.SetQueryData(TodoList, new List<Todo>());
        await sync.StartAsync();

        _server.Network.Break(); // without automatic reconnect the connection closes with an error
        await Wait.UntilAsync(() => sync.Status == QuerySyncStatus.Disconnected);
        _server.Network.Restore();
        await Wait.UntilAsync(() => connection.State == HubConnectionState.Connected && sync.Status == QuerySyncStatus.Synchronized);

        await _server.PublishAsync(new Todo(1, "a"));
        await Wait.UntilAsync(() => client.GetQueryData<List<Todo>>(TodoList).Data!.Count == 1);
    }

    [Fact]
    public async Task The_source_disposes_only_a_connection_it_owns()
    {
        var shared = _server.CreateConnection();
        await shared.StartAsync();
        var borrowing = new HubConnectionQueryEventSource(shared);
        var subscription = borrowing.Subscribe<QueryEventEnvelope<Todo>>("TodoChanged", _ => default);
        await borrowing.DisposeAsync();
        subscription.Dispose();
        Assert.Equal(HubConnectionState.Connected, shared.State);

        var dedicated = _server.CreateConnection();
        var owning = new HubConnectionQueryEventSource(dedicated, new HubConnectionQueryEventSourceOptions { OwnsConnection = true });
        await owning.StartAsync();
        await owning.DisposeAsync();
        Assert.Equal(HubConnectionState.Disconnected, dedicated.State);
        Assert.Throws<ObjectDisposedException>(() => owning.Subscribe<int>("x", _ => default));
    }

    [Fact]
    public async Task Disposing_during_an_active_recovery_completes_promptly()
    {
        var recovering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (_, sync, _, connection) = Setup(syncOptions: new QueryEventSyncOptions
        {
            Recovery = QuerySyncRecovery.Custom(async (_, ct) =>
            {
                recovering.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return true;
            }),
        });
        MapTodos(sync);
        await sync.StartAsync();
        await recovering.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await sync.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(QuerySyncStatus.Disconnected, sync.Status);
        Assert.Equal(HubConnectionState.Connected, connection.State); // not owned: left running
    }

    [Fact]
    public async Task A_burst_of_hub_events_is_processed_in_order_without_loss()
    {
        const int Count = 2000;
        var (client, sync, _, _) = Setup(syncOptions: new QueryEventSyncOptions { ContiguousSequences = true });
        client.SetQueryData(Todos.Append("last"), 0L);
        sync.Map<QueryEventEnvelope<Todo>>("TodoChanged", m => m.Update<long>(Todos.Append("last"), (last, e) =>
            e.Sequence == last + 1 ? e.Sequence!.Value : throw new InvalidOperationException($"out of order: {e.Sequence} after {last}")));
        await sync.StartAsync();
        await Wait.UntilAsync(() => sync.Status == QuerySyncStatus.Synchronized);

        for (var i = 0; i < Count; i++)
        {
            await _server.PublishAsync(new Todo(i, "x"));
        }

        await Wait.UntilAsync(() => client.GetQueryData<long>(Todos.Append("last")).Data == Count, timeoutMs: 60000);
        var stats = sync.GetStatistics();
        Assert.Equal(Count, stats.Applied);
        Assert.Equal(0, stats.Failed + stats.Gaps + stats.Dropped);
    }

    [Fact]
    public async Task Dependency_injection_registers_the_synchronizer_with_the_client_lifetime()
    {
        var services = new ServiceCollection();
        services.AddDotNetQuery(options => options.GarbageCollectionInterval = Timeout.InfiniteTimeSpan)
            .AddSignalRSync(
                _ => _server.CreateConnection(),
                sync => MapTodos(sync),
                new HubConnectionQueryEventSourceOptions { OwnsConnection = true });
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IQueryClient>();
        client.SetQueryData(TodoList, new List<Todo>());

        var sync = provider.GetRequiredService<QueryEventSynchronizer>();
        Assert.Same(sync, provider.GetRequiredService<QueryEventSynchronizer>());
        await sync.StartAsync();
        await _server.PublishAsync(new Todo(1, "a"));
        await Wait.UntilAsync(() => client.GetQueryData<List<Todo>>(TodoList).Data!.Count == 1);

        var connection = provider.GetRequiredService<HubConnectionQueryEventSource>().Connection;
        await provider.DisposeAsync();
        Assert.Equal(HubConnectionState.Disconnected, connection.State);
    }
}
