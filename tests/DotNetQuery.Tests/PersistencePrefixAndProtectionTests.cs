using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using DotNetQuery;
using DotNetQuery.Persistence;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Tests;

public class PersistencePrefixAndProtectionTests
{
    private sealed record Todo(int Id, string Title);

    private static readonly JsonTypeInfo<Todo> TodoInfo = (JsonTypeInfo<Todo>)TestUtil.WebJson.GetTypeInfo(typeof(Todo));
    private static readonly QueryKey TodoPrefix = QueryKey.Create("todo");

    private readonly FakeTimeProvider _time = new();

    private QueryClient CreateClient() => new(TestUtil.NoRetry(o => o.DefaultStaleTime = TimeSpan.FromMinutes(5)), _time);

    [Fact]
    public async Task Prefixed_queries_persist_and_restore_on_first_use_without_a_fetch()
    {
        var store = new InMemoryQueryPersister();
        using (var first = CreateClient())
        using (var manager = new QueryPersistenceManager(first, store, new QueryPersistenceOptions { TimeProvider = _time }).RegisterPrefix(TodoPrefix, TodoInfo))
        {
            await first.FetchAsync(TodoPrefix.Append(1), _ => Task.FromResult(new Todo(1, "milk")));
            await first.FetchAsync(TodoPrefix.Append(2), _ => Task.FromResult(new Todo(2, "eggs")));
            await first.FetchAsync(QueryKey.Create("other"), _ => Task.FromResult(new Todo(3, "not persisted")));
            await manager.PersistAllAsync();
        }

        Assert.Equal(2, (await store.LoadAllAsync()).Count);

        using var second = CreateClient();
        using var restoring = new QueryPersistenceManager(second, store, new QueryPersistenceOptions { TimeProvider = _time }).RegisterPrefix(TodoPrefix, TodoInfo);
        Assert.Equal(0, await restoring.RestoreAllAsync()); // nothing cached yet: records wait for first use
        var calls = 0;

        var todo = await second.FetchAsync(TodoPrefix.Append(2), _ => { calls++; return Task.FromResult(new Todo(2, "from server")); });

        Assert.Equal("eggs", todo.Data!.Title);
        Assert.Equal(0, calls); // hydrated before the fetch checked freshness
    }

    [Fact]
    public async Task Prefixed_records_apply_immediately_to_queries_already_cached()
    {
        var store = new InMemoryQueryPersister();
        using (var first = CreateClient())
        using (var manager = new QueryPersistenceManager(first, store).RegisterPrefix(TodoPrefix, TodoInfo))
        {
            first.SetQueryData(TodoPrefix.Append(7), new Todo(7, "saved"));
            await manager.PersistAllAsync();
        }

        using var second = CreateClient();
        using var observer = second.Watch<Todo>(TodoPrefix.Append(7));
        using var restoring = new QueryPersistenceManager(second, store).RegisterPrefix(TodoPrefix, TodoInfo);

        Assert.Equal(1, await restoring.RestoreAllAsync());
        Assert.Equal("saved", observer.CurrentState.Data!.Title);
    }

    [Fact]
    public async Task Auto_persist_covers_prefixed_queries_and_skips_other_types()
    {
        var store = new InMemoryQueryPersister();
        using var client = CreateClient();
        using var manager = new QueryPersistenceManager(client, store, new QueryPersistenceOptions { TimeProvider = _time }).RegisterPrefix(TodoPrefix, TodoInfo);
        manager.StartAutoPersist(TimeSpan.FromMilliseconds(10));

        client.SetQueryData(TodoPrefix.Append(1), new Todo(1, "auto"));
        client.SetQueryData(TodoPrefix.Append("count"), 3); // same prefix, different type: not persisted
        await manager.FlushAutoPersistAsync();

        var stored = Assert.Single(await store.LoadAllAsync());
        Assert.Equal("[\"todo\", 1]", stored.Key);
    }

    [Fact]
    public async Task Expired_prefixed_records_are_dropped_on_first_use()
    {
        var store = new InMemoryQueryPersister();
        await store.SaveAsync(new PersistedQuery("[\"todo\", 5]", JsonSerializer.Serialize(new Todo(5, "old"), TodoInfo), _time.GetUtcNow() - TimeSpan.FromDays(10)));
        using var client = CreateClient();
        using var manager = new QueryPersistenceManager(client, store, new QueryPersistenceOptions { TimeProvider = _time, MaxAge = TimeSpan.FromDays(1) })
            .RegisterPrefix(TodoPrefix, TodoInfo);
        await manager.RestoreAllAsync();

        var result = await client.FetchAsync(TodoPrefix.Append(5), _ => Task.FromResult(new Todo(5, "fresh")));

        Assert.Equal("fresh", result.Data!.Title);
        await TestUtil.WaitUntilAsync(() => store.LoadAllAsync().Result.Count == 0);
    }

    [Fact]
    public async Task Protected_persister_stores_transformed_payloads_and_restores_them()
    {
        var inner = new InMemoryQueryPersister();
        var protectedStore = new ProtectedQueryPersister(inner, Encode, Decode);
        using (var first = CreateClient())
        using (var manager = new QueryPersistenceManager(first, protectedStore).Register(TodoPrefix.Append(1), TodoInfo))
        {
            first.SetQueryData(TodoPrefix.Append(1), new Todo(1, "secret"));
            await manager.PersistAllAsync();
        }

        var raw = Assert.Single(await inner.LoadAllAsync());
        Assert.DoesNotContain("secret", raw.Payload);

        using var second = CreateClient();
        using var restoring = new QueryPersistenceManager(second, protectedStore).Register(TodoPrefix.Append(1), TodoInfo);
        Assert.Equal(1, await restoring.RestoreAllAsync());
        Assert.Equal("secret", second.GetQueryData<Todo>(TodoPrefix.Append(1)).Data!.Title);
    }

    [Fact]
    public async Task Records_that_cannot_be_unprotected_are_skipped_and_reported()
    {
        var inner = new InMemoryQueryPersister();
        await inner.SaveAsync(new PersistedQuery("good", Encode("{\"id\":1,\"title\":\"ok\"}"), DateTimeOffset.UtcNow));
        await inner.SaveAsync(new PersistedQuery("bad", "not base64!", DateTimeOffset.UtcNow));
        var store = new ProtectedQueryPersister(inner, Encode, Decode);
        var errors = new List<string>();
        store.Error += (key, _) => errors.Add(key);

        var loaded = await store.LoadAllAsync();

        Assert.Equal(["good"], loaded.Select(r => r.Key));
        Assert.Null(await store.LoadAsync("bad"));
        Assert.Equal(["bad", "bad"], errors);
    }

    [Fact]
    public async Task Durable_mutation_queue_variables_are_protected_too()
    {
        var inner = new InMemoryQueryPersister();
        var connectivity = new ConnectivityProvider();
        connectivity.SetOnline(false);
        using var queue = new PersistentMutationQueue(new ProtectedQueryPersister(inner, Encode, Decode), connectivity)
            .RegisterHandler<Todo>("todos/add", (_, _) => Task.CompletedTask, TodoInfo);

        await queue.EnqueueAsync("todos/add", new Todo(1, "card number 4111"));

        Assert.DoesNotContain("4111", Assert.Single(await inner.LoadAllAsync()).Payload);
    }

    private static string Encode(string plain) => Convert.ToBase64String(Encoding.UTF8.GetBytes(plain));

    private static string Decode(string stored) => Encoding.UTF8.GetString(Convert.FromBase64String(stored));
}
