using System.Text.Json.Serialization;
using DotNetQuery;
using DotNetQuery.Persistence;
using DotNetQuery.Demo.OfflineFirst;

// An offline-first walkthrough: persist the cache online, restart offline and restore it, keep working with writes
// queued durably, close the app, start again, reconnect and replay - in order, once, with idempotency keys - and let a
// rejected write land in the dead-letter queue. Deterministic and self-terminating, so it doubles as an end-to-end smoke
// test (exit 0 == pass).

var api = new TodoApi();
var cacheStore = new InMemoryQueryPersister(); // stands in for a durable store (file system, SQLite, IndexedDB)
var queueStore = new InMemoryQueryPersister(); // the queue gets a store of its own
var key = QueryKey.Create("todos");

PersistentMutationQueue CreateQueue(IConnectivityProvider connectivity) =>
    new PersistentMutationQueue(queueStore, connectivity, new PersistentMutationQueueOptions
        {
            MaxAttempts = 3,
            ShouldRetry = HttpRetryPolicy.ShouldRetry,
            UseDeadLetterQueue = true,
            // A conflict (409/412) cannot succeed by retrying: keep it for the user instead.
            OnReplayFailure = (_, error, _) => HttpRetryPolicy.IsConflict(error) ? ReplayDecision.Fail : ReplayDecision.Default,
        })
        // The queued mutation's stable id travels as an idempotency key, so a replay after a crash is not applied twice.
        .RegisterHandler<string>("todos/add", (text, mutation, ct) => api.AddTodoAsync(text, mutation.Id, ct), DemoJson.Default.String);

// --- Session 1 (online): fetch and persist --------------------------------------------------------
Console.WriteLine("=== Session 1 (online): fetch + persist ===");
using (var client1 = new QueryClient(connectivity: new ConnectivityProvider()))
{
    var persistence1 = new QueryPersistenceManager(client1, cacheStore).Register(key, DemoJson.Default.ListString);
    var result = await client1.FetchAsync(key, api.GetTodosAsync);
    await persistence1.PersistAllAsync();
    Console.WriteLine($"Fetched {result.Data!.Count} todos; persisted to the store.\n");
}

// --- Session 2 (starts OFFLINE): restore, work offline, quit --------------------------------------
Console.WriteLine("=== Session 2 (starts offline): restore + work offline ===");
var offline = new ConnectivityProvider();
offline.SetOnline(false);
List<string> startupData;
bool offlineServedFromCache;
using (var client2 = new QueryClient(connectivity: offline))
using (var queue2 = CreateQueue(offline))
{
    var persistence2 = new QueryPersistenceManager(client2, cacheStore).Register(key, DemoJson.Default.ListString);
    var restored = await persistence2.RestoreAllAsync();
    startupData = client2.GetQueryData<List<string>>(key).Data!;
    Console.WriteLine($"Restored {restored} query from the store: {startupData.Count} todos available at startup, offline.");

    var readsBefore = api.Reads;
    var offlineFetch = await client2.FetchAsync(key, api.GetTodosAsync);
    offlineServedFromCache = api.Reads == readsBefore;
    Console.WriteLine($"[offline] FetchAsync served {offlineFetch.Data!.Count} todos from cache; server reads unchanged: {offlineServedFromCache}.");

    await queue2.EnqueueAsync("todos/add", "Write release notes");
    await queue2.EnqueueAsync("todos/add", "conflict: rename a deleted todo");
    await queue2.EnqueueAsync("todos/add", "Tag v1.0");
    Console.WriteLine($"[offline] queued 3 writes durably (pending: {queue2.PendingCount}); server still has {api.ServerCount}.");
    Console.WriteLine("[offline] the app is closed with writes still queued.\n");
}

// --- Session 3: start again, reconnect, replay ------------------------------------------------------
Console.WriteLine("=== Session 3: restart, reconnect, replay ===");
var connectivity = new ConnectivityProvider();
connectivity.SetOnline(false);
using var client = new QueryClient(connectivity: connectivity);
using var queue = CreateQueue(connectivity);
var failures = new List<string>();
queue.MutationFailed += (mutation, error) => failures.Add(error.Message);

var restoredWrites = await queue.RestoreAsync();
Console.WriteLine($"Restored {restoredWrites} queued writes from the previous session.");

connectivity.SetOnline(true);
await queue.FlushAsync(); // deterministic; the reconnect auto-flush may already have drained it
Console.WriteLine($"[online] replayed the queue in order (pending: {queue.PendingCount}); server now has {api.ServerCount}.");
foreach (var dead in queue.DeadLetters)
{
    Console.WriteLine($"[online] dead letter: '{dead.Mutation.MutationKey}' - {dead.Error} (retry or discard it from the UI)");
}

var synced = await client.FetchAsync(key, api.GetTodosAsync);
Console.WriteLine($"[online] fetched {synced.Data!.Count} todos: {string.Join(", ", synced.Data)}.");

var ok = startupData.Count == 3
    && offlineServedFromCache                 // offline read hit the cache, not the network
    && restoredWrites == 3                     // the writes survived the restart
    && queue.PendingCount == 0
    && queue.DeadLetters.Count == 1            // the conflict waits for the user
    && synced.Data!.SequenceEqual(["Buy milk", "Walk the dog", "Ship DotNetQuery 1.0", "Write release notes", "Tag v1.0"])
    && api.AppliedKeys == 2;                   // each accepted write applied once
Console.WriteLine(ok ? "\nDONE (pass)" : "\nDONE (FAIL)");
return ok ? 0 : 1;

[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(string))]
internal sealed partial class DemoJson : JsonSerializerContext;
