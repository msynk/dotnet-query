using System.Runtime.CompilerServices;
using DotNetQuery;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Core.Tests;

public class QueryStreamTests
{
    private static QueryClient CreateClient() => new(new QueryClientOptions(), new FakeTimeProvider());

    private static async IAsyncEnumerable<int> Numbers(int count)
    {
        for (var i = 1; i <= count; i++)
        {
            await Task.Yield();
            yield return i;
        }
    }

    private static async IAsyncEnumerable<string> Letters()
    {
        foreach (var s in new[] { "a", "b", "c" })
        {
            await Task.Yield();
            yield return s;
        }
    }

    private static async IAsyncEnumerable<int> InfiniteWithDelay([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var i = 0;
        while (true)
        {
            await Task.Delay(15, cancellationToken);
            yield return ++i;
        }
    }

    [Fact]
    public async Task StreamIntoCache_replaces_cached_value_for_each_emission_and_notifies_observers()
    {
        using var client = CreateClient();
        var key = QueryKey.Create("live");
        var seen = new List<int>();
        using var observer = client.Watch<int>(key);
        observer.StateChanged += s => { if (s.HasData) seen.Add(s.Data); };

        await client.StreamIntoCacheAsync(key, Numbers(3));

        Assert.Equal([1, 2, 3], seen);
        Assert.Equal(3, client.GetQueryData<int>(key).Data);
    }

    [Fact]
    public async Task StreamIntoCache_accumulate_folds_items_into_a_growing_list()
    {
        using var client = CreateClient();
        var key = QueryKey.Create("chat");

        await client.StreamIntoCacheAsync<List<string>, string>(
            key, Letters(), (list, message) => [.. list ?? [], message]);

        Assert.Equal(["a", "b", "c"], client.GetQueryData<List<string>>(key).Data);
    }

    [Fact]
    public async Task StreamIntoCache_stops_and_throws_on_cancellation()
    {
        using var client = CreateClient();
        var key = QueryKey.Create("cancel-stream");
        using var cts = new CancellationTokenSource();

        // The helper flows its cancellationToken to the enumerator via WithCancellation.
        var task = client.StreamIntoCacheAsync(key, InfiniteWithDelay(), cts.Token);
        await Task.Delay(60);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(client.GetQueryData<int>(key).HasData); // values arrived before cancellation
    }
}
