using DotNetQuery;
using DotNetQuery.Persistence;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Tests;

public class MorePersistenceAndInfiniteTests
{
    private static QueryClient CreateClient() => new(new QueryClientOptions(), new FakeTimeProvider());

    [Fact]
    public async Task InMemory_persister_supports_load_remove_clear()
    {
        var persister = new InMemoryQueryPersister();
        await persister.SaveAsync(new PersistedQuery("a", "1", DateTimeOffset.UnixEpoch));
        await persister.SaveAsync(new PersistedQuery("b", "2", DateTimeOffset.UnixEpoch));

        Assert.Equal("1", (await persister.LoadAsync("a"))!.Payload);
        Assert.Null(await persister.LoadAsync("missing"));
        Assert.Equal(2, (await persister.LoadAllAsync()).Count);

        await persister.RemoveAsync("a");
        Assert.Null(await persister.LoadAsync("a"));

        await persister.ClearAsync();
        Assert.Empty(await persister.LoadAllAsync());
    }

    [Fact]
    public async Task Manager_persist_and_restore_single_key()
    {
        var persister = new InMemoryQueryPersister();
        var key = QueryKey.Create("single");

        using var client1 = CreateClient();
        var m1 = new QueryPersistenceManager(client1, persister).Register<int>(key);
        client1.SetQueryData(key, 123);
        await m1.PersistAsync(key);

        using var client2 = CreateClient();
        var m2 = new QueryPersistenceManager(client2, persister).Register<int>(key);
        var restored = await m2.RestoreAsync(key);

        Assert.True(restored);
        Assert.Equal(123, client2.GetQueryData<int>(key).Data);
    }

    [Fact]
    public async Task Manager_persist_unregistered_key_is_noop()
    {
        var persister = new InMemoryQueryPersister();
        using var client = CreateClient();
        var manager = new QueryPersistenceManager(client, persister); // nothing registered

        await manager.PersistAsync(QueryKey.Create("nope"));

        Assert.Empty(await persister.LoadAllAsync());
    }

    [Fact]
    public async Task Infinite_query_retries_a_failing_page()
    {
        using var client = CreateClient();
        var attempts = 0;
        using var query = client.CreateInfiniteQuery<int, int>(
            QueryKey.Create("retry-feed"),
            (param, _) =>
            {
                attempts++;
                if (attempts < 3)
                {
                    throw new TimeoutException();
                }

                return Task.FromResult(param);
            },
            new InfiniteQueryOptions<int, int>
            {
                InitialPageParam = 0,
                RetryCount = 5,
                RetryDelay = RetryPolicy.Constant(TimeSpan.Zero),
                GetNextPageParam = (_, _, _) => NextPageParam.None<int>(),
            });

        var state = await query.FetchAsync();

        Assert.Equal(3, attempts);
        Assert.True(state.IsSuccess);
        Assert.Single(state.Data.Pages);
    }
}
