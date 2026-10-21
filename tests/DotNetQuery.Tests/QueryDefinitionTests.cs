using DotNetQuery;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static DotNetQuery.Tests.TestUtil;

namespace DotNetQuery.Tests;

public class QueryDefinitionTests
{
    private sealed record Todo(int Id, string Title);

    private sealed class TodoApi
    {
        public int Calls;

        public Task<List<Todo>> GetAllAsync(CancellationToken _)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new List<Todo> { new(1, "a") });
        }

        public Task<Todo> GetAsync(int id, CancellationToken _)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new Todo(id, $"todo {id}"));
        }
    }

    private static class TodoQueries
    {
        public static readonly QueryKey All = QueryKey.Create("todos");

        public static QueryDefinition<List<Todo>> List(TodoApi api) =>
            new(All.Append("list"), api.GetAllAsync, new QueryOptions { StaleTime = TimeSpan.FromMinutes(1) });

        public static QueryDefinition<Todo> ById(TodoApi api, int id) => new(All.Append(id), ct => api.GetAsync(id, ct));
    }

    [Fact]
    public async Task Definitions_drive_fetch_cache_reads_and_writes()
    {
        using var client = new QueryClient(NoRetry(), new FakeTimeProvider());
        var api = new TodoApi();

        var first = await client.FetchAsync(TodoQueries.List(api));
        var second = await client.FetchAsync(TodoQueries.List(api)); // definition options: fresh for 1 min

        Assert.Single(first.Data!);
        Assert.Same(first.Data, second.Data);
        Assert.Equal(1, api.Calls);

        client.SetQueryData(TodoQueries.ById(api, 5), new Todo(5, "edited"));
        Assert.Equal("edited", client.GetQueryData(TodoQueries.ById(api, 5)).Data!.Title);
    }

    [Fact]
    public async Task EnsureQueryData_returns_cached_data_or_fetches_and_throws_on_failure()
    {
        using var client = new QueryClient(NoRetry(), new FakeTimeProvider());
        var api = new TodoApi();

        var todo = await client.EnsureQueryDataAsync(TodoQueries.ById(api, 3));
        var again = await client.EnsureQueryDataAsync(TodoQueries.ById(api, 3));
        Assert.Same(todo, again);
        Assert.Equal(1, api.Calls);

        var failing = new QueryDefinition<int>(QueryKey.Create("broken"), _ => throw new InvalidOperationException("nope"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.EnsureQueryDataAsync(failing));
    }

    [Fact]
    public async Task Invalidating_a_parent_key_refreshes_definitions_beneath_it()
    {
        using var client = new QueryClient(NoRetry(), new FakeTimeProvider());
        var api = new TodoApi();
        using var listObserver = client.Watch(TodoQueries.List(api));
        using var itemObserver = client.Watch(TodoQueries.ById(api, 1));
        await client.FetchAsync(TodoQueries.List(api));
        await client.FetchAsync(TodoQueries.ById(api, 1));

        await client.InvalidateQueriesAsync(TodoQueries.All);

        Assert.Equal(4, api.Calls);
    }

    [Fact]
    public void WithOptions_transforms_a_copy()
    {
        var api = new TodoApi();
        var original = TodoQueries.List(api);

        var disabled = original.WithOptions(o => o with { Enabled = false });

        Assert.False(disabled.Options!.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(1), disabled.Options.StaleTime);
        Assert.Null(original.Options!.Enabled);
    }

    [Fact]
    public void KeepPreviousData_shows_the_previous_page_while_the_next_one_loads()
    {
        using var client = new QueryClient(NoRetry(), new FakeTimeProvider());
        client.SetQueryData(QueryKey.Create("page", 1), "page one");
        var previous = client.GetQueryData<string>(QueryKey.Create("page", 1));
        var pending = client.GetQueryData<string>(QueryKey.Create("page", 2));

        var shown = pending.KeepPreviousData(previous);

        Assert.True(shown.IsPlaceholderData);
        Assert.Equal("page one", shown.Data);
        Assert.False(shown.IsPending);
        Assert.True(shown.IsFetching);
        Assert.Equal(QueryKey.Create("page", 2), shown.Key);

        client.SetQueryData(QueryKey.Create("page", 2), "page two");
        var loaded = client.GetQueryData<string>(QueryKey.Create("page", 2)).KeepPreviousData(previous);
        Assert.False(loaded.IsPlaceholderData);
        Assert.Equal("page two", loaded.Data);
    }

    [Fact]
    public void Default_result_is_safe_to_read()
    {
        QueryResult<int> result = default;

        Assert.Equal(QueryStatus.Idle, result.Status);
        Assert.True(result.IsPending);
    }
}
