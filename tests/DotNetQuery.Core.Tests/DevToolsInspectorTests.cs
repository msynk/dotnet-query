using System.Text.Json;
using DotNetQuery;
using DotNetQuery.DevTools;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static DotNetQuery.Core.Tests.TestUtil;

namespace DotNetQuery.Core.Tests;

public class DevToolsInspectorTests
{
    private sealed record Todo(int Id, string Title);

    [Fact]
    public async Task Mutation_history_tracks_pending_then_settled_runs()
    {
        using var client = new QueryClient(NoRetry(), new FakeTimeProvider());
        using var devtools = new QueryDevTools(client);
        var release = NewSignal<int>();
        var key = QueryKey.Create("todos", "add");

        var run = client.MutateAsync<string, int>((_, _) => release.Task, "milk", new MutationOptions<string, int> { MutationKey = key });
        var pending = Assert.Single(devtools.RecentMutations());
        Assert.Equal(MutationStatus.Pending, pending.Status);
        Assert.Equal(key, pending.MutationKey);
        Assert.Equal("milk", pending.Variables);
        Assert.Equal(1, devtools.GetStats().PendingMutations);

        release.SetResult(1);
        await run;
        await client.MutateAsync<string, int>((_, _) => Task.FromException<int>(new InvalidOperationException("nope")), "eggs", new MutationOptions<string, int> { ThrowOnError = false });

        var history = devtools.RecentMutations();
        Assert.Equal(2, history.Count);
        Assert.Equal(MutationStatus.Error, history[0].Status); // most recent first
        Assert.Equal("nope", history[0].ErrorMessage);
        Assert.Equal(MutationStatus.Success, history[1].Status);
        Assert.NotNull(history[1].Duration);
        Assert.Equal(1, devtools.GetStats().MutationsFailed);
    }

    [Fact]
    public async Task Mutation_history_is_bounded()
    {
        using var client = new QueryClient(NoRetry(), new FakeTimeProvider());
        using var devtools = new QueryDevTools(client, mutationHistoryCapacity: 3);

        for (var i = 0; i < 5; i++)
        {
            await client.MutateAsync<int, int>((v, _) => Task.FromResult(v), i);
        }

        var history = devtools.RecentMutations();
        Assert.Equal(3, history.Count);
        Assert.Equal(4, history[0].Variables);
    }

    [Fact]
    public void PreviewData_renders_cached_data_as_json()
    {
        using var client = new QueryClient(NoRetry(), new FakeTimeProvider());
        using var devtools = new QueryDevTools(client);
        client.SetQueryData(QueryKey.Create("todo", 1), new Todo(1, "write docs"));

        var preview = devtools.PreviewData(QueryKey.Create("todo", 1));

        Assert.NotNull(preview);
        using var doc = JsonDocument.Parse(preview!);
        Assert.Equal("write docs", doc.RootElement.GetProperty("title").GetString());
        Assert.Null(devtools.PreviewData(QueryKey.Create("missing")));
    }

    [Fact]
    public void Changed_fires_for_cache_writes_that_raise_no_diagnostic_event()
    {
        using var client = new QueryClient(NoRetry(), new FakeTimeProvider());
        using var devtools = new QueryDevTools(client);
        var changed = 0;
        devtools.Changed += () => Interlocked.Increment(ref changed);

        client.SetQueryData(QueryKey.Create("x"), 1);

        Assert.True(changed > 0);
    }

    [Fact]
    public async Task Stats_count_cancelled_fetches_and_in_flight_queries()
    {
        using var client = new QueryClient(NoRetry(), new FakeTimeProvider());
        using var devtools = new QueryDevTools(client);
        var started = NewSignal();
        var key = QueryKey.Create("slow");

        var fetch = client.FetchAsync(key, Gated(started, NewSignal<int>().Task));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, devtools.GetStats().FetchingQueries);

        await devtools.CancelAsync(key);
        await fetch;

        var stats = devtools.GetStats();
        Assert.Equal(0, stats.FetchingQueries);
        Assert.Equal(1, stats.FetchesCancelled);
    }

    [Fact]
    public async Task Export_includes_the_mutation_history()
    {
        using var client = new QueryClient(NoRetry(), new FakeTimeProvider());
        using var devtools = new QueryDevTools(client);
        await client.MutateAsync<int, int>((v, _) => Task.FromResult(v), 1, new MutationOptions<int, int> { MutationKey = QueryKey.Create("m") });

        using var doc = JsonDocument.Parse(devtools.ExportJson());

        var mutation = doc.RootElement.GetProperty("mutations")[0];
        Assert.Equal("[\"m\"]", mutation.GetProperty("mutationKey").GetString());
        Assert.Equal("Success", mutation.GetProperty("status").GetString());
    }
}
