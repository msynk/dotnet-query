// NativeAOT / trimming verification harness.
//
// This is not a functional sample - it exists so `dotnet publish -p:PublishAot=true` compiles a
// program that instantiates the generic-heavy hot paths of DotNetQuery (cache, observers,
// mutations, infinite queries) and reports zero IL2xxx/IL3xxx trim/AOT warnings. Any warning fails
// the build (TreatWarningsAsErrors) and thus CI.

using System.Text.Json;
using System.Text.Json.Serialization;
using DotNetQuery;
using DotNetQuery.Persistence;

int failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {what}");
    if (!ok)
    {
        failures++;
    }
}

using var client = new QueryClient();

// --- Keys ---------------------------------------------------------------------------------------
var key = QueryKey.Create<User>("profile", 42);
Check(key.Count == 3 && key[0] as string == "User", "query key construction");

// --- Basic fetch + cache hit --------------------------------------------------------------------
var fetchCount = 0;
Task<User> Fetch(CancellationToken _)
{
    Interlocked.Increment(ref fetchCount);
    return Task.FromResult(new User(42, "Ada"));
}

var options = new QueryOptions { StaleTime = TimeSpan.FromMinutes(5) };
var first = await client.FetchAsync(key, Fetch, options);
Check(first.IsSuccess && first.Data!.Name == "Ada", "cold fetch");

var second = await client.FetchAsync(key, Fetch, options);
Check(second.IsSuccess && fetchCount == 1, "cached hit skips fetcher");

var forced = await client.FetchAsync(key, Fetch, options, forceRefetch: true);
Check(forced.IsSuccess && fetchCount == 2, "forced refetch runs fetcher");

// --- Observers ----------------------------------------------------------------------------------
var notifications = 0;
using (var observer = client.Watch<User>(key))
{
    observer.StateChanged += _ => Interlocked.Increment(ref notifications);
    client.SetQueryData(key, new User(42, "Grace"));
}

Check(notifications >= 1, "observer notified on SetQueryData");
Check(client.GetQueryData<User>(key).Data!.Name == "Grace", "imperative cache write");

// --- Invalidation -------------------------------------------------------------------------------
await client.InvalidateQueriesAsync(QueryKey.Create<User>());
Check(true, "prefix invalidation");

// --- Mutations ----------------------------------------------------------------------------------
var mutation = await client.MutateAsync<string, int>(
    (name, _) => Task.FromResult(name.Length),
    "Turing",
    new MutationOptions<string, int>
    {
        OnMutate = (_, _) => new ValueTask<object?>((object?)null),
        OnSuccess = (_, _, _, _) => ValueTask.CompletedTask,
        OnSettled = (_, _, _, _, _) => ValueTask.CompletedTask,
    });
Check(mutation.IsSuccess && mutation.Data == 6, "mutation lifecycle");

// --- Infinite query -----------------------------------------------------------------------------
using (var infinite = client.CreateInfiniteQuery<int, Page>(
    QueryKey.Create("feed"),
    (pageParam, _) => Task.FromResult(new Page(pageParam, Enumerable.Range(pageParam * 10, 10).ToArray())),
    new InfiniteQueryOptions<int, Page>
    {
        InitialPageParam = 0,
        GetNextPageParam = (last, _, _) => last.Index < 2 ? NextPageParam.Of(last.Index + 1) : NextPageParam.None<int>(),
    }))
{
    var page0 = await infinite.FetchAsync();
    Check(page0.Data.Pages.Count == 1, "infinite first page");
    var page1 = await infinite.FetchNextPageAsync();
    Check(page1.Data.Pages.Count == 2, "infinite next page");
}

// --- Query definitions, cache writes, hydration, cancellation ----------------------------------
var userQuery = new QueryDefinition<User>(QueryKey.Create<User>("by-id", 7), _ => Task.FromResult(new User(7, "Hopper")));
var hopper = await client.EnsureQueryDataAsync(userQuery);
Check(hopper.Name == "Hopper", "query definition + EnsureQueryData");

var renamed = client.SetQueryData(userQuery, u => u! with { Name = "Grace Hopper" });
Check(renamed.Name == "Grace Hopper", "atomic SetQueryData updater");

Check(client.HydrateQueryData(QueryKey.Create("hydrated"), 42, DateTimeOffset.UtcNow), "hydration");
await client.CancelQueriesAsync(QueryKey.Create("hydrated"));
Check(client.GetQueryData<int>(QueryKey.Create("hydrated")).Data == 42, "cancel without in-flight fetch");

// --- Declarative optimistic update + invalidation ----------------------------------------------
var listKey = QueryKey.Create("names");
client.SetQueryData(listKey, new List<string> { "Ada" });
var failed = await client.MutateAsync<string, string>(
    (_, _) => Task.FromException<string>(new InvalidOperationException("rejected")),
    "Linus",
    new MutationOptions<string, string>
    {
        ThrowOnError = false,
        OptimisticUpdates = [OptimisticUpdate.Create<string, List<string>>(listKey, (names, name) => [.. names ?? [], name])],
        Invalidates = [listKey],
    });
Check(failed.IsError && client.GetQueryData<List<string>>(listKey).Data!.Count == 1, "optimistic update rollback");

// --- Persistence with source-generated JSON (trim/AOT-safe path) -------------------------------
var store = new InMemoryQueryPersister();
using (var manager = new QueryPersistenceManager(client, store).Register(userQuery, HarnessJson.Default.User))
{
    await manager.PersistAllAsync();
}

using (var restoredClient = new QueryClient())
{
    var manager = new QueryPersistenceManager(restoredClient, store).Register(userQuery, HarnessJson.Default.User);
    Check(await manager.RestoreAllAsync() == 1 && restoredClient.GetQueryData(userQuery).Data!.Name == "Grace Hopper", "persist + restore");
}

var envelope = PersistedQueryJson.Serialize(new PersistedQuery("k", "\"payload\"", DateTimeOffset.UnixEpoch));
Check(PersistedQueryJson.Deserialize(envelope) is { Payload: "\"payload\"" }, "persisted envelope round-trip");

// --- Durable offline mutation queue ------------------------------------------------------------
var connectivity = new ConnectivityProvider();
connectivity.SetOnline(false);
var replayed = 0;
using (var queue = new PersistentMutationQueue(new InMemoryQueryPersister(), connectivity))
{
    queue.RegisterHandler<User>("users/save", (_, _) => { Interlocked.Increment(ref replayed); return Task.CompletedTask; }, HarnessJson.Default.User);
    await queue.EnqueueAsync("users/save", new User(1, "Offline"));
    connectivity.SetOnline(true);
    await queue.FlushAsync();
    Check(replayed == 1 && queue.PendingCount == 0, "persistent mutation queue replay");
}

// --- HTTP transport with source-generated JSON -------------------------------------------------
using var http = new HttpClient(new StubHandler()) { BaseAddress = new Uri("https://example.test/") };
var remote = await client.FetchAsync(http.JsonQuery(QueryKey.Create("remote-user"), "users/5", HarnessJson.Default.User));
Check(remote.IsSuccess && remote.Data!.Name == "Remote", "HTTP JSON query (source-generated)");

Console.WriteLine(failures == 0
    ? "\nAOT harness: all checks passed."
    : $"\nAOT harness: {failures} check(s) FAILED.");
return failures;

internal sealed record User(int Id, string Name);
internal sealed record Page(int Index, int[] Items);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(User))]
internal sealed partial class HarnessJson : JsonSerializerContext;

internal sealed class StubHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":5,\"name\":\"Remote\"}", System.Text.Encoding.UTF8, "application/json"),
        });
}
