using System.Text.Json.Serialization.Metadata;
using DotNetQuery;
using DotNetQuery.Synchronization;
using Xunit;

namespace DotNetQuery.Tests;

/// <summary>
/// Integration tests for <see cref="RedisQuerySyncChannel"/>; they skip without a reachable Redis server
/// (<c>DOTNETQUERY_REDIS</c>, default <c>localhost:6379</c>), like the Redis persistence tests.
/// </summary>
public sealed class RedisSyncChannelTests : IClassFixture<RedisPersistenceTests.RedisFixture>
{
    private static readonly JsonTypeInfo<string> StringInfo = (JsonTypeInfo<string>)TestUtil.WebJson.GetTypeInfo(typeof(string));

    private readonly RedisPersistenceTests.RedisFixture _fixture;

    public RedisSyncChannelTests(RedisPersistenceTests.RedisFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Messages_reach_every_other_subscriber_in_order()
    {
        Skip.If(_fixture.Connection is null, $"Redis not reachable at {RedisPersistenceTests.RedisFixture.Endpoint}");
        var name = "dnq:test:sync:" + Guid.NewGuid().ToString("N");
        await using var a = await RedisQuerySyncChannel.CreateAsync(_fixture.Connection!, name);
        await using var b = await RedisQuerySyncChannel.CreateAsync(_fixture.Connection!, name);
        var received = new List<string>();
        b.MessageReceived += m => { lock (received) { received.Add(m); } };

        for (var i = 0; i < 20; i++)
        {
            await a.PostAsync($"m{i}");
        }

        await TestUtil.WaitUntilAsync(() => { lock (received) { return received.Count == 20; } });
        lock (received)
        {
            Assert.Equal(Enumerable.Range(0, 20).Select(i => $"m{i}"), received);
        }
    }

    [SkippableFact]
    public async Task Two_clients_stay_in_sync_through_redis()
    {
        Skip.If(_fixture.Connection is null, $"Redis not reachable at {RedisPersistenceTests.RedisFixture.Endpoint}");
        var name = "dnq:test:sync:" + Guid.NewGuid().ToString("N");
        await using var channelA = await RedisQuerySyncChannel.CreateAsync(_fixture.Connection!, name);
        await using var channelB = await RedisQuerySyncChannel.CreateAsync(_fixture.Connection!, name);
        using var serverA = new QueryClient(TestUtil.NoRetry());
        using var serverB = new QueryClient(TestUtil.NoRetry());
        using var syncA = new QuerySynchronizer(serverA, channelA).Register(QueryKey.Create("motd"), StringInfo).Start();
        using var syncB = new QuerySynchronizer(serverB, channelB).Register(QueryKey.Create("motd"), StringInfo).Start();

        serverA.SetQueryData(QueryKey.Create("motd"), "hello from A");

        await TestUtil.WaitUntilAsync(() => serverB.GetQueryData<string>(QueryKey.Create("motd")).Data == "hello from A");
    }
}
