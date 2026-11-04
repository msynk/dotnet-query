using System.Net.Http.Json;
using DotNetQuery;
using DotNetQuery.Sample.Realtime;
using DotNetQuery.SignalR;
using DotNetQuery.Synchronization;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

// Real-time cache synchronization end to end: an ASP.NET Core server with a SignalR hub publishes versioned, sequenced
// todo changes; a client keeps its query cache in sync through Msynk.DotNetQuery.SignalR. Deterministic and
// self-verifying (exit 0 == pass), so it doubles as a CI smoke test.

// ------------------------------------------------------------------ server
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.ClearProviders();
builder.Services.AddSignalR();
builder.Services.AddSingleton<TodoStore>();
var app = builder.Build();
var store = app.Services.GetRequiredService<TodoStore>();
app.MapHub<TodoHub>("/hubs/todos");
app.MapGet("/api/todos", () => store.All());
app.MapGet("/api/todos/{id:int}", (int id) => store.Get(id) is { } todo ? Results.Ok(todo) : Results.NotFound());
app.MapGet("/api/stats", () => store.Stats());
app.MapPut("/api/todos/{id:int}", async (int id, RenameRequest request, HttpRequest http) =>
{
    // The server broadcasts the change before it answers (the response is slow) - the client must reconcile both.
    var envelope = await store.ChangeAsync(id, t => t with { Title = request.Title }, http.Headers["X-Correlation-Id"]);
    await Task.Delay(300);
    return Results.Ok(envelope.Payload);
});
await app.StartAsync();
var baseAddress = new Uri(app.Urls.First());

// ------------------------------------------------------------------ client
var failures = 0;
var network = new SimulatedNetwork();
using var http = new HttpClient { BaseAddress = baseAddress };
using var client = new QueryClient(new QueryClientOptions { DefaultStaleTime = TimeSpan.FromMinutes(5), DefaultRetryCount = 0 });
int listFetches = 0, detailFetches = 0, statsFetches = 0;
var list = new QueryDefinition<List<Todo>>(TodoQueries.List, async ct =>
{
    Interlocked.Increment(ref listFetches);
    return (await http.GetFromJsonAsync<List<Todo>>("api/todos", ct))!;
});
var detail = new QueryDefinition<Todo>(TodoQueries.ById(1), async ct =>
{
    Interlocked.Increment(ref detailFetches);
    return (await http.GetFromJsonAsync<Todo>("api/todos/1", ct))!;
});
var stats = new QueryDefinition<TodoStats>(TodoQueries.Stats, async ct =>
{
    Interlocked.Increment(ref statsFetches);
    return (await http.GetFromJsonAsync<TodoStats>("api/stats", ct))!;
});

var connection = new HubConnectionBuilder()
    .WithUrl(new Uri(baseAddress, "hubs/todos"), options =>
    {
        options.Transports = HttpTransportType.LongPolling;
        options.HttpMessageHandlerFactory = network.Wrap; // only to simulate an outage below
    })
    .WithAutomaticReconnect(new SteadyRetry())
    .Build();
var source = new HubConnectionQueryEventSource(connection, new HubConnectionQueryEventSourceOptions
{
    OwnsConnection = true,
    OnConnected = (hub, ct) => hub.InvokeAsync("JoinWorkspace", TodoStore.Workspace, ct), // groups do not survive reconnects
});
await using var sync = new QueryEventSynchronizer(client, source, new QueryEventSyncOptions
{
    Name = "todos",
    Scope = TodoStore.Workspace,
    ContiguousSequences = true,
    // The server retains its history, so missed events are replayed; without it the recovery invalidates instead.
    Recovery = QuerySyncRecovery.Replay(async (context, ct) =>
    {
        // No checkpoint yet (first connect): nothing to resume from - fall back to invalidating what is cached.
        if (context.GetCheckpoint() is not { } checkpoint)
        {
            return false;
        }

        var missed = await connection.InvokeAsync<List<QueryEventEnvelope<Todo>>>("EventsSince", checkpoint, ct);
        Console.WriteLine($"  recovery: replaying {missed.Count} missed event(s) after checkpoint {checkpoint}");
        foreach (var envelope in missed)
        {
            await context.ApplyAsync("TodoChanged", envelope, ct);
        }

        return true;
    }),
});
sync.StatusChanged += change => Console.WriteLine($"  status: {change.Previous} -> {change.Status}{(change.Error is null ? null : $" ({change.Error.Message})")}");

// One mapping: upsert the changed todo into every cached list and its detail (newer versions only, envelopes carry
// id/version/sequence/scope), and invalidate the stats, which the event cannot update incrementally.
sync.Map<QueryEventEnvelope<Todo>>("TodoChanged", m => m
    .Upsert(TodoQueries.Entity, e => e.Payload, EntityMissingBehavior.Invalidate)
    .Invalidate(TodoQueries.Stats));

// A second "browser tab" without its own connection follows through the relay (BroadcastChannel in Blazor).
var tabs = new InMemoryQuerySyncHub();
using var relay = new QueryEventRelay(sync, tabs.CreateChannel()).Start();
using var followerClient = new QueryClient(new QueryClientOptions { DefaultStaleTime = TimeSpan.FromMinutes(5) });
await using var follower = new QueryEventSynchronizer(followerClient, options: new QueryEventSyncOptions { Name = "follower-tab", Scope = TodoStore.Workspace });
follower.Map<QueryEventEnvelope<Todo>>("TodoChanged", m => m.Upsert(TodoQueries.Entity, e => e.Payload));
using var followerRelay = new QueryEventRelay(follower, tabs.CreateChannel()).Start();
var followerList = new QueryDefinition<List<Todo>>(TodoQueries.List, async ct => (await http.GetFromJsonAsync<List<Todo>>("api/todos", ct))!);
using var followerView = followerClient.Watch(followerList);
await followerClient.FetchAsync(followerList);

Console.WriteLine("=== 1. Connect, then load two independent views of the same cache ===");
await sync.StartAsync();
await Until(() => sync.Status == QuerySyncStatus.Synchronized, "synchronized after connecting");
using var listView = client.Watch(list);
using var detailView = client.Watch(detail);
using var statsView = client.Watch(stats);
listView.StateChanged += s => { if (s.HasData) { Console.WriteLine($"  list view:   {string.Join(" | ", s.Data!.Select(t => $"{t.Id}:{t.Title}{(t.Done ? " (done)" : null)} v{t.Version}"))}"); } };
detailView.StateChanged += s => { if (s.HasData) { Console.WriteLine($"  detail view: {s.Data!.Title} v{s.Data.Version}"); } };
await client.FetchAsync(list);
await client.FetchAsync(detail);
await client.FetchAsync(stats);

Console.WriteLine("\n=== 2. Another user edits todo 1: incremental update, no refetch of the list or detail ===");
await store.ChangeAsync(1, t => t with { Title = "Write the spec (v2)" });
await Until(() => client.GetQueryData(list).Data![0].Title == "Write the spec (v2)", "the list shows the change");
await Until(() => client.GetQueryData(detail).Data!.Title == "Write the spec (v2)", "the detail view shows the same change");
Check(listFetches == 1 && detailFetches == 1, $"list and detail were updated in place (fetches: list {listFetches}, detail {detailFetches})");
await Until(() => statsFetches == 2, "the stats query was invalidated and refetched (targeted invalidation)");

Console.WriteLine("\n=== 3. Our optimistic rename races the server's broadcast of it ===");
const string Correlation = "rename-1";
var rename = client.CreateMutation<string, Todo>(
    async (title, ct) =>
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "api/todos/1") { Content = JsonContent.Create(new RenameRequest(title)) };
        request.Headers.Add("X-Correlation-Id", Correlation);
        using var response = await http.SendAsync(request, ct);
        return (await response.EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Todo>(ct))!;
    },
    new MutationOptions<string, Todo>
    {
        OptimisticUpdates =
        [
            OptimisticUpdate.Create<string, List<Todo>>(TodoQueries.List, (todos, title) => [.. todos!.Select(t => t.Id == 1 ? t with { Title = title } : t)]),
            OptimisticUpdate.Create<string, Todo>(TodoQueries.ById(1), (todo, title) => todo! with { Title = title }),
        ],
    });
var pending = rename.MutateAsync("Write the spec (final)");
Check(client.GetQueryData(list).Data![0].Title == "Write the spec (final)", "the optimistic title shows immediately");
await Until(() => client.GetQueryData(detail).Data!.Version == 3, "the broadcast (version 3) arrived before the slow response and replaced the optimistic value");
var saved = await pending;
Check(saved.Data!.Version == 3 && client.GetQueryData(list).Data![0] == saved.Data, "the response matches the cache: nothing flickers back");

Console.WriteLine("\n=== 4. The network drops; two changes are missed; reconnecting replays them ===");
var listFetchesBefore = listFetches;
network.Drop();
await Until(() => sync.Status == QuerySyncStatus.Reconnecting, "reconnecting");
await store.ChangeAsync(2, t => t with { Done = true });
await store.ChangeAsync(3, t => t with { Title = "Ship it (Friday)" });
network.Restore();
await Until(() => sync.Status == QuerySyncStatus.Synchronized, "rejoined the workspace group and synchronized");
await Until(() => client.GetQueryData(list).Data is [_, { Done: true }, { Title: "Ship it (Friday)" }], "the missed changes were replayed into the list");
Check(listFetches == listFetchesBefore, "replay needed no refetch of the list");
Check(sync.GetCheckpoint() == 4, $"checkpoint advanced to sequence {sync.GetCheckpoint()} (four changes so far)");

Console.WriteLine("\n=== 5. A redelivered old event is discarded ===");
var old = store.Since(0)[0];
await store.BroadcastAsync(old);
await Until(() => sync.GetStatistics().Duplicates >= 1, "the duplicate was detected by its event id");
Check(client.GetQueryData(detail).Data!.Title == "Write the spec (final)", "the cache kept the newer state");

Console.WriteLine("\n=== 6. The follower tab refreshed from relayed notices (no payloads cross tabs) ===");
await Until(() => followerClient.GetQueryData(followerList).Data is [{ Title: "Write the spec (final)" }, { Done: true }, { Title: "Ship it (Friday)" }], "the follower's list caught up");
Check(followerRelay.MessagesApplied > 0 && followerRelay.MessagesSent == 0, $"notices applied: {followerRelay.MessagesApplied}, relayed back: {followerRelay.MessagesSent}");

var final = sync.GetStatistics();
Console.WriteLine($"\nSynchronizer: received {final.Received}, applied {final.Applied}, duplicates {final.Duplicates}, stale {final.Stale}, " +
                  $"gaps {final.Gaps}, failed {final.Failed}, recoveries {final.Recoveries} ({final.RecoveryFallbacks} fell back).");
Check(final.Failed == 0 && final.Dropped == 0, "no failed or dropped events");

// Disposing the synchronizer and its owned source stops the connection before the server goes away.
await sync.DisposeAsync();
await source.DisposeAsync();
await app.StopAsync();
Console.WriteLine(failures == 0 ? "\nAll checks passed." : $"\n{failures} check(s) FAILED.");
return failures == 0 ? 0 : 1;

void Check(bool ok, string what)
{
    Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {what}");
    if (!ok)
    {
        failures++;
    }
}

async Task Until(Func<bool> condition, string what, int timeoutMs = 15000)
{
    var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
    while (!SafeCheck(condition) && DateTime.UtcNow < deadline)
    {
        await Task.Delay(20);
    }

    Check(SafeCheck(condition), what);
}

static bool SafeCheck(Func<bool> condition)
{
    try
    {
        return condition();
    }
    catch (Exception)
    {
        return false;
    }
}

internal sealed record RenameRequest(string Title);

internal static class TodoQueries
{
    public static readonly QueryKey All = QueryKey.Create("todos");
    public static readonly QueryKey List = All.Append("list");
    public static readonly QueryKey Stats = QueryKey.Create("stats");

    /// <summary>Todos are identified by id and ordered by version; details live under <see cref="ById"/>.</summary>
    public static readonly QueryEntity<Todo, int> Entity = QueryEntity.Create<Todo, int>(All, t => t.Id, version: t => t.Version, detailKey: ById);

    public static QueryKey ById(int id) => All.Append("by-id", id);
}
