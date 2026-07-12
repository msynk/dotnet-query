using DotNetQuery;
using DotNetQuery.Persistence;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Core.Tests;

public class SqlitePersistenceTests
{
    public sealed record Todo(int Id, string Title, bool Done);

    private static QueryClient CreateClient() => new(new QueryClientOptions(), new FakeTimeProvider());

    [Fact]
    public async Task Save_Load_roundtrips_a_row()
    {
        await using var persister = new SqliteQueryPersister("Data Source=:memory:");
        var updated = new DateTimeOffset(2026, 7, 12, 8, 30, 0, TimeSpan.Zero);

        await persister.SaveAsync(new PersistedQuery("users:1", "{\"id\":1}", updated));

        var loaded = await persister.LoadAsync("users:1");
        Assert.NotNull(loaded);
        Assert.Equal("users:1", loaded!.Key);
        Assert.Equal("{\"id\":1}", loaded.Payload);
        Assert.Equal(updated, loaded.UpdatedAt); // timestamp fidelity through the string column
    }

    [Fact]
    public async Task Load_returns_null_for_missing_key()
    {
        await using var persister = new SqliteQueryPersister("Data Source=:memory:");
        Assert.Null(await persister.LoadAsync("nope"));
    }

    [Fact]
    public async Task Save_upserts_on_conflicting_key()
    {
        await using var persister = new SqliteQueryPersister("Data Source=:memory:");

        await persister.SaveAsync(new PersistedQuery("k", "v1", DateTimeOffset.UnixEpoch));
        await persister.SaveAsync(new PersistedQuery("k", "v2", DateTimeOffset.UnixEpoch));

        var all = await persister.LoadAllAsync();
        Assert.Single(all);
        Assert.Equal("v2", all[0].Payload);
    }

    [Fact]
    public async Task LoadAll_Remove_and_Clear()
    {
        await using var persister = new SqliteQueryPersister("Data Source=:memory:");
        await persister.SaveAsync(new PersistedQuery("a", "1", DateTimeOffset.UnixEpoch));
        await persister.SaveAsync(new PersistedQuery("b", "2", DateTimeOffset.UnixEpoch));

        Assert.Equal(2, (await persister.LoadAllAsync()).Count);

        await persister.RemoveAsync("a");
        Assert.Null(await persister.LoadAsync("a"));
        Assert.Single(await persister.LoadAllAsync());

        await persister.ClearAsync();
        Assert.Empty(await persister.LoadAllAsync());
    }

    [Fact]
    public async Task End_to_end_persist_and_restore_across_clients_via_a_file_database()
    {
        var path = Path.Combine(Path.GetTempPath(), "dnq-tests", Guid.NewGuid().ToString("N") + ".db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var key = QueryKey.Create("profile");

            // Session 1: populate and persist, then close the connection.
            await using (var persister1 = SqliteQueryPersister.ForFile(path))
            using (var client1 = CreateClient())
            {
                var manager1 = new QueryPersistenceManager(client1, persister1).Register<Todo>(key);
                client1.SetQueryData(key, new Todo(99, "persist me", true));
                await manager1.PersistAllAsync();
            }

            // Session 2: fresh persister + client over the same file, restore.
            await using var persister2 = SqliteQueryPersister.ForFile(path);
            using var client2 = CreateClient();
            var manager2 = new QueryPersistenceManager(client2, persister2).Register<Todo>(key);
            var restored = await manager2.RestoreAllAsync();

            Assert.Equal(1, restored);
            Assert.Equal(new Todo(99, "persist me", true), client2.GetQueryData<Todo>(key).Data);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task Concurrent_saves_are_serialized_without_error()
    {
        await using var persister = new SqliteQueryPersister("Data Source=:memory:");

        var writes = Enumerable.Range(0, 50).Select(i =>
            persister.SaveAsync(new PersistedQuery($"k{i}", i.ToString(), DateTimeOffset.UnixEpoch)));
        await Task.WhenAll(writes);

        Assert.Equal(50, (await persister.LoadAllAsync()).Count);
    }
}
