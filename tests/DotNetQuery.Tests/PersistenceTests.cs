using DotNetQuery;
using DotNetQuery.Persistence;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Tests;

public class PersistenceTests
{
    public sealed record Todo(int Id, string Title, bool Done);

    private static QueryClient CreateClient() => new(new QueryClientOptions(), new FakeTimeProvider());

    [Fact]
    public async Task Persist_and_restore_roundtrips_data_through_a_new_client()
    {
        var persister = new InMemoryQueryPersister();
        var key = QueryKey.Create("todos");
        var todos = new List<Todo> { new(1, "a", false), new(2, "b", true) };

        // Session 1: populate and persist.
        using (var client1 = CreateClient())
        {
            var manager1 = new QueryPersistenceManager(client1, persister).Register<List<Todo>>(key);
            client1.SetQueryData(key, todos);
            await manager1.PersistAllAsync();
        }

        // Session 2: fresh client, restore from the persister.
        using var client2 = CreateClient();
        var manager2 = new QueryPersistenceManager(client2, persister).Register<List<Todo>>(key);
        var restored = await manager2.RestoreAllAsync();

        Assert.Equal(1, restored);
        var data = client2.GetQueryData<List<Todo>>(key).Data;
        Assert.NotNull(data);
        Assert.Equal(2, data!.Count);
        Assert.Equal("b", data[1].Title);
        Assert.True(data[1].Done);
    }

    [Fact]
    public async Task Restore_returns_false_when_no_persisted_data()
    {
        var persister = new InMemoryQueryPersister();
        using var client = CreateClient();
        var manager = new QueryPersistenceManager(client, persister).Register<int>(QueryKey.Create("missing"));

        Assert.False(await manager.RestoreAsync(QueryKey.Create("missing")));
    }

    [Fact]
    public async Task PersistAll_skips_queries_without_data()
    {
        var persister = new InMemoryQueryPersister();
        using var client = CreateClient();
        var manager = new QueryPersistenceManager(client, persister).Register<int>(QueryKey.Create("empty"));

        await manager.PersistAllAsync();

        Assert.Empty(await persister.LoadAllAsync());
    }

    [Fact]
    public async Task FileSystem_persister_roundtrips_via_disk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dnq-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var persister = new FileSystemQueryPersister(dir);
            var key = QueryKey.Create("user", 7);

            await persister.SaveAsync(new PersistedQuery(key.ToString(), "{\"id\":7}", DateTimeOffset.UnixEpoch));

            var loaded = await persister.LoadAsync(key.ToString());
            Assert.NotNull(loaded);
            Assert.Equal("{\"id\":7}", loaded!.Payload);

            var all = await persister.LoadAllAsync();
            Assert.Single(all);

            await persister.RemoveAsync(key.ToString());
            Assert.Null(await persister.LoadAsync(key.ToString()));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task FileSystem_persister_end_to_end_with_manager()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dnq-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var key = QueryKey.Create("profile");
            var persister = new FileSystemQueryPersister(dir);

            using (var client1 = CreateClient())
            {
                var manager1 = new QueryPersistenceManager(client1, persister).Register<Todo>(key);
                client1.SetQueryData(key, new Todo(99, "persist me", true));
                await manager1.PersistAllAsync();
            }

            using var client2 = CreateClient();
            var manager2 = new QueryPersistenceManager(client2, persister).Register<Todo>(key);
            await manager2.RestoreAllAsync();

            var restored = client2.GetQueryData<Todo>(key).Data;
            Assert.Equal(new Todo(99, "persist me", true), restored);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
