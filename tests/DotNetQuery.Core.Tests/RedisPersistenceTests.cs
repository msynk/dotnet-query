using DotNetQuery;
using DotNetQuery.Persistence;
using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;
using Xunit;

namespace DotNetQuery.Core.Tests;

/// <summary>
/// Integration tests for <see cref="RedisQueryPersister"/>. They connect to the Redis endpoint in the
/// <c>DOTNETQUERY_REDIS</c> environment variable (default <c>localhost:6379</c>) and <b>skip</b> when no
/// server is reachable, so the suite stays green without Redis. Start one with:
/// <c>docker run --rm -p 6379:6379 redis:7-alpine</c>.
/// </summary>
public sealed class RedisPersistenceTests : IClassFixture<RedisPersistenceTests.RedisFixture>
{
    private readonly RedisFixture _fixture;

    public RedisPersistenceTests(RedisFixture fixture) => _fixture = fixture;

    public sealed record Todo(int Id, string Title, bool Done);

    private static QueryClient CreateClient() => new(new QueryClientOptions(), new FakeTimeProvider());

    // A fresh hash key per test isolates concurrently-running cases.
    private RedisQueryPersister NewPersister(out string hashKey)
    {
        Skip.If(_fixture.Connection is null, $"Redis not reachable at {RedisFixture.Endpoint}");
        hashKey = "dnq:test:" + Guid.NewGuid().ToString("N");
        return new RedisQueryPersister(_fixture.Connection!, hashKey);
    }

    [SkippableFact]
    public async Task Save_Load_roundtrips_with_timestamp_fidelity()
    {
        var persister = NewPersister(out _);
        try
        {
            var updated = new DateTimeOffset(2026, 7, 12, 8, 30, 0, TimeSpan.Zero);
            await persister.SaveAsync(new PersistedQuery("users:1", "{\"id\":1}", updated));

            var loaded = await persister.LoadAsync("users:1");
            Assert.NotNull(loaded);
            Assert.Equal("{\"id\":1}", loaded!.Payload);
            Assert.Equal(updated, loaded.UpdatedAt);
        }
        finally
        {
            await persister.ClearAsync();
        }
    }

    [SkippableFact]
    public async Task Load_returns_null_for_missing_key()
    {
        var persister = NewPersister(out _);
        try
        {
            Assert.Null(await persister.LoadAsync("nope"));
        }
        finally
        {
            await persister.ClearAsync();
        }
    }

    [SkippableFact]
    public async Task Save_upserts_LoadAll_Remove_and_Clear()
    {
        var persister = NewPersister(out _);
        try
        {
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
        finally
        {
            await persister.ClearAsync();
        }
    }

    [SkippableFact]
    public async Task End_to_end_persist_and_restore_across_clients()
    {
        var persister = NewPersister(out _);
        try
        {
            var key = QueryKey.Create("profile");

            using (var client1 = CreateClient())
            {
                var manager1 = new QueryPersistenceManager(client1, persister).Register<Todo>(key);
                client1.SetQueryData(key, new Todo(99, "persist me", true));
                await manager1.PersistAllAsync();
            }

            using var client2 = CreateClient();
            var manager2 = new QueryPersistenceManager(client2, persister).Register<Todo>(key);
            var restored = await manager2.RestoreAllAsync();

            Assert.Equal(1, restored);
            Assert.Equal(new Todo(99, "persist me", true), client2.GetQueryData<Todo>(key).Data);
        }
        finally
        {
            await persister.ClearAsync();
        }
    }

    /// <summary>Connects once per test class; leaves <see cref="Connection"/> null when Redis is absent.</summary>
    public sealed class RedisFixture : IAsyncLifetime
    {
        public static string Endpoint =>
            Environment.GetEnvironmentVariable("DOTNETQUERY_REDIS") ?? "localhost:6379";

        public IConnectionMultiplexer? Connection { get; private set; }

        public async Task InitializeAsync()
        {
            try
            {
                var options = ConfigurationOptions.Parse(Endpoint);
                options.AbortOnConnectFail = false;
                options.ConnectTimeout = 1500;
                options.ConnectRetry = 1;
                var connection = await ConnectionMultiplexer.ConnectAsync(options);
                Connection = connection.IsConnected ? connection : null;
                if (Connection is null)
                {
                    await connection.DisposeAsync();
                }
            }
            catch
            {
                Connection = null;
            }
        }

        public async Task DisposeAsync()
        {
            if (Connection is not null)
            {
                await Connection.DisposeAsync();
            }
        }
    }
}
