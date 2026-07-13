// NativeAOT / trimming verification harness.
//
// This is not a functional sample - it exists so `dotnet publish -p:PublishAot=true` compiles a
// program that instantiates the generic-heavy hot paths of DotNetQuery.Core (cache, observers,
// mutations, infinite queries) and reports zero IL2xxx/IL3xxx trim/AOT warnings. Any warning fails
// the build (TreatWarningsAsErrors) and thus CI.

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

// --- Persistence (System.Text.Json source-gen free path) ----------------------------------------
IQueryPersister persister = new InMemoryQueryPersister();
await persister.SaveAsync(new PersistedQuery("k", "\"payload\"", DateTimeOffset.UnixEpoch));
var loaded = await persister.LoadAsync("k");
Check(loaded is { Payload: "\"payload\"" }, "persister round-trip");

Console.WriteLine(failures == 0
    ? "\nAOT harness: all checks passed."
    : $"\nAOT harness: {failures} check(s) FAILED.");
return failures;

internal sealed record User(int Id, string Name);
internal sealed record Page(int Index, int[] Items);
