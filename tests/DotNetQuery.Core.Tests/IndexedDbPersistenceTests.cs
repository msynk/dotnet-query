using DotNetQuery;
using DotNetQuery.Persistence;
using Microsoft.Extensions.Time.Testing;
using Microsoft.JSInterop;
using Xunit;

namespace DotNetQuery.Core.Tests;

public class IndexedDbPersistenceTests
{
    public sealed record Todo(int Id, string Title, bool Done);

    private static QueryClient CreateClient() => new(new QueryClientOptions(), new FakeTimeProvider());

    [Fact]
    public async Task Save_Load_roundtrips_with_timestamp_fidelity()
    {
        var persister = new IndexedDbQueryPersister(new FakeIndexedDbJsRuntime());
        var updated = new DateTimeOffset(2026, 7, 12, 8, 30, 0, TimeSpan.Zero);

        await persister.SaveAsync(new PersistedQuery("users:1", "{\"id\":1}", updated));

        var loaded = await persister.LoadAsync("users:1");
        Assert.NotNull(loaded);
        Assert.Equal("{\"id\":1}", loaded!.Payload);
        Assert.Equal(updated, loaded.UpdatedAt);
    }

    [Fact]
    public async Task Load_returns_null_for_missing_key()
    {
        var persister = new IndexedDbQueryPersister(new FakeIndexedDbJsRuntime());
        Assert.Null(await persister.LoadAsync("nope"));
    }

    [Fact]
    public async Task Save_upserts_LoadAll_Remove_and_Clear()
    {
        var persister = new IndexedDbQueryPersister(new FakeIndexedDbJsRuntime());

        await persister.SaveAsync(new PersistedQuery("a", "1", DateTimeOffset.UnixEpoch));
        await persister.SaveAsync(new PersistedQuery("b", "2", DateTimeOffset.UnixEpoch));
        await persister.SaveAsync(new PersistedQuery("a", "1-updated", DateTimeOffset.UnixEpoch)); // upsert

        var all = await persister.LoadAllAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal("1-updated", all.Single(q => q.Key == "a").Payload);

        await persister.RemoveAsync("a");
        Assert.Null(await persister.LoadAsync("a"));
        Assert.Single(await persister.LoadAllAsync());

        await persister.ClearAsync();
        Assert.Empty(await persister.LoadAllAsync());
    }

    [Fact]
    public async Task Persisted_data_survives_a_new_persister_over_the_same_store()
    {
        var store = new FakeIndexedDbJsRuntime(); // shared backing store simulates IndexedDB across reloads
        var key = QueryKey.Create("profile");

        using (var client1 = CreateClient())
        {
            var manager1 = new QueryPersistenceManager(client1, new IndexedDbQueryPersister(store)).Register<Todo>(key);
            client1.SetQueryData(key, new Todo(99, "persist me", true));
            await manager1.PersistAllAsync();
        }

        using var client2 = CreateClient();
        var manager2 = new QueryPersistenceManager(client2, new IndexedDbQueryPersister(store)).Register<Todo>(key);
        var restored = await manager2.RestoreAllAsync();

        Assert.Equal(1, restored);
        Assert.Equal(new Todo(99, "persist me", true), client2.GetQueryData<Todo>(key).Data);
    }

    /// <summary>An in-memory stand-in for the browser IndexedDB module reached over <see cref="IJSRuntime"/>.</summary>
    private sealed class FakeIndexedDbJsRuntime : IJSRuntime
    {
        private readonly Dictionary<string, string> _store = new(StringComparer.Ordinal);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            switch (identifier)
            {
                case "dotnetQueryIndexedDb.save": // (db, store, key, json)
                    _store[(string)args![2]!] = (string)args[3]!;
                    return new ValueTask<TValue>(default(TValue)!);
                case "dotnetQueryIndexedDb.load": // (db, store, key)
                    _store.TryGetValue((string)args![2]!, out var value);
                    return new ValueTask<TValue>((TValue)(object?)value!);
                case "dotnetQueryIndexedDb.loadAll": // (db, store)
                    return new ValueTask<TValue>((TValue)(object)_store.Values.ToArray());
                case "dotnetQueryIndexedDb.remove": // (db, store, key)
                    _store.Remove((string)args![2]!);
                    return new ValueTask<TValue>(default(TValue)!);
                case "dotnetQueryIndexedDb.clear": // (db, store)
                    _store.Clear();
                    return new ValueTask<TValue>(default(TValue)!);
                default:
                    throw new NotSupportedException(identifier);
            }
        }
    }
}
