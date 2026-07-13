using DotNetQuery;
using DotNetQuery.Persistence;
using DotNetQuery.Sample.OfflineFirst;

// An offline-first walkthrough: persist online, restart offline and restore, keep working (queueing
// mutations) with no network, then reconnect and sync. Deterministic and self-terminating so it doubles
// as an end-to-end smoke test (exit 0 == pass).

var api = new TodoApi();
var store = new InMemoryQueryPersister(); // stands in for a durable store shared across app sessions
var key = QueryKey.Create("todos");

// --- Session 1 (online): fetch and persist --------------------------------------------------------
Console.WriteLine("=== Session 1 (online): fetch + persist ===");
using (var client1 = new QueryClient(connectivity: new ConnectivityProvider()))
{
    var persistence1 = new QueryPersistenceManager(client1, store).Register<List<string>>(key);
    var result = await client1.FetchAsync(key, api.GetTodosAsync);
    await persistence1.PersistAllAsync();
    Console.WriteLine($"Fetched {result.Data!.Count} todos; persisted to the store.\n");
}

// --- Session 2 (starts OFFLINE): restore, work offline --------------------------------------------
Console.WriteLine("=== Session 2 (starts offline): restore + work offline ===");
var connectivity = new ConnectivityProvider();
connectivity.SetOnline(false);
using var client = new QueryClient(connectivity: connectivity);
var persistence = new QueryPersistenceManager(client, store).Register<List<string>>(key);
using var queue = new OfflineMutationQueue(connectivity);

var restored = await persistence.RestoreAllAsync();
var startupData = client.GetQueryData<List<string>>(key);
Console.WriteLine($"Restored {restored} query from the store: {startupData.Data!.Count} todos available at startup, offline.");

var readsBefore = api.Reads;
var offlineFetch = await client.FetchAsync(key, api.GetTodosAsync);
var offlineServedFromCache = api.Reads == readsBefore; // captured now, before any later online refetch
Console.WriteLine($"[offline] FetchAsync served {offlineFetch.Data!.Count} todos from cache; server reads unchanged: {offlineServedFromCache}.");

await queue.EnqueueAsync(ct => api.AddTodoAsync("Write release notes", ct));
Console.WriteLine($"[offline] queued 'add todo' (pending: {queue.PendingCount}); server still has {api.ServerCount}.\n");

// --- Reconnect: replay the queue and sync ---------------------------------------------------------
Console.WriteLine("=== Back online: replay queue + sync ===");
connectivity.SetOnline(true);
await queue.FlushAsync(); // deterministic; the reconnect auto-flush may already have drained it
Console.WriteLine($"[online] replayed queue (pending: {queue.PendingCount}); server now has {api.ServerCount}.");

var synced = await client.FetchAsync(key, api.GetTodosAsync, forceRefetch: true);
Console.WriteLine($"[online] refetched {synced.Data!.Count} todos.");

var ok = startupData.Data.Count == 3
    && offlineServedFromCache            // offline read hit the cache, not the network
    && queue.PendingCount == 0
    && synced.Data!.Count == 4;          // the queued mutation replayed and the refetch reflects it
Console.WriteLine(ok ? "\nDONE (pass)" : "\nDONE (FAIL)");
return ok ? 0 : 1;
