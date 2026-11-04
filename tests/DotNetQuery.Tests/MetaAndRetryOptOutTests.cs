using DotNetQuery;
using Xunit;

namespace DotNetQuery.Tests;

public class MetaAndRetryOptOutTests
{
    [Fact]
    public async Task A_mutation_can_opt_out_of_retries_enabled_by_its_defaults()
    {
        var options = TestUtil.NoRetry()
            .SetMutationDefaults(QueryKey.Create("todos"), new MutationDefaults { RetryCount = 3 });
        using var client = new QueryClient(options);
        var defaulted = 0;
        var optedOut = 0;

        await client.CreateMutation<int, int>((_, _) => { defaulted++; throw new TimeoutException(); },
            new() { MutationKey = QueryKey.Create("todos", "add"), ThrowOnError = false }).MutateAsync(1);
        await client.CreateMutation<int, int>((_, _) => { optedOut++; throw new TimeoutException(); },
            new() { MutationKey = QueryKey.Create("todos", "add"), RetryCount = 0, ThrowOnError = false }).MutateAsync(1);

        Assert.Equal(4, defaulted);
        Assert.Equal(1, optedOut);
    }

    [Fact]
    public async Task Infinite_query_retry_count_defaults_to_zero_and_can_be_set()
    {
        using var client = new QueryClient(TestUtil.NoRetry());
        var calls = 0;
        using var noRetry = client.CreateInfiniteQuery<int, int>(QueryKey.Create("a"), (_, _) => { calls++; throw new TimeoutException(); },
            new InfiniteQueryOptions<int, int> { InitialPageParam = 0, GetNextPageParam = (_, _, _) => NextPageParam.None<int>() });
        await noRetry.FetchAsync();
        Assert.Equal(1, calls);

        calls = 0;
        using var twoRetries = client.CreateInfiniteQuery<int, int>(QueryKey.Create("b"), (_, _) => { calls++; throw new TimeoutException(); },
            new InfiniteQueryOptions<int, int> { InitialPageParam = 0, GetNextPageParam = (_, _, _) => NextPageParam.None<int>(), RetryCount = 2 });
        await twoRetries.FetchAsync();
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Query_meta_reaches_descriptors_and_fetch_events_and_layers_by_prefix()
    {
        var options = TestUtil.NoRetry()
            .SetQueryDefaults(QueryKey.Create("todos"), new QueryOptions { Meta = new Dictionary<string, object?> { ["errorMessage"] = "Could not load todos" } });
        using var client = new QueryClient(options);
        var events = new List<DotNetQueryEvent>();
        client.DiagnosticEvent += e => { lock (events) { events.Add(e); } };

        await client.FetchAsync(QueryKey.Create("todos", 1), _ => Task.FromResult(1));
        await client.FetchAsync(QueryKey.Create("todos", 2), _ => Task.FromException<int>(new InvalidOperationException("boom")));
        await client.FetchAsync(QueryKey.Create("todos", 3), _ => Task.FromResult(3),
            new QueryOptions { Meta = new Dictionary<string, object?> { ["silent"] = true } });

        Assert.Equal("Could not load todos", client.Cache.Describe(QueryKey.Create("todos", 1))!.Meta!["errorMessage"]);
        Assert.Equal(true, client.Cache.Describe(QueryKey.Create("todos", 3))!.Meta!["silent"]);
        Assert.False(client.Cache.Describe(QueryKey.Create("todos", 3))!.Meta!.ContainsKey("errorMessage"));

        lock (events)
        {
            var failed = Assert.Single(events.OfType<QueryFailedEvent>());
            Assert.Equal("Could not load todos", failed.Meta!["errorMessage"]);
            Assert.All(events.OfType<QueryCompletedEvent>(), e => Assert.NotNull(e.Meta));
        }
    }

    [Fact]
    public async Task Mutation_meta_reaches_events_and_the_mutation_cache_and_falls_back_to_defaults()
    {
        var options = TestUtil.NoRetry()
            .SetMutationDefaults(new MutationDefaults { Meta = new Dictionary<string, object?> { ["toast"] = "default" } });
        using var client = new QueryClient(options);
        var events = new List<DotNetQueryEvent>();
        client.DiagnosticEvent += e => { lock (events) { events.Add(e); } };

        await client.CreateMutation<int, int>((v, _) => Task.FromResult(v)).MutateAsync(1);
        await client.CreateMutation<int, int>((_, _) => throw new InvalidOperationException(),
            new() { Meta = new Dictionary<string, object?> { ["toast"] = "Saving failed" }, ThrowOnError = false }).MutateAsync(2);

        lock (events)
        {
            Assert.Equal(["default", "Saving failed"], events.OfType<MutationStartedEvent>().Select(e => (string)e.Meta!["toast"]!));
            var failed = events.OfType<MutationCompletedEvent>().Single(e => !e.Succeeded);
            Assert.Equal("Saving failed", failed.Meta!["toast"]);
        }

        Assert.Equal(["default", "Saving failed"], client.Cache.Mutations.FindAll().OrderBy(m => m.MutationId).Select(m => (string)m.Meta!["toast"]!));
    }
}
