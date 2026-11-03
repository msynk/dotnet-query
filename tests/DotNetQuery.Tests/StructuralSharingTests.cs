using DotNetQuery;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Tests;

public class StructuralSharingTests
{
    private sealed record Todo(int Id, string Title, bool Done);

    private sealed class ThrowingEquals
    {
        public override bool Equals(object? obj) => throw new InvalidOperationException("broken Equals");

        public override int GetHashCode() => 0;
    }

    private static QueryClient CreateClient(bool sharingByDefault = false) =>
        new(TestUtil.NoRetry(o => o.DefaultStructuralSharing = sharingByDefault), new FakeTimeProvider());

    private static readonly QueryOptions Sharing = new() { StructuralSharing = true };

    [Fact]
    public async Task Disabled_by_default_so_refetches_store_the_new_instance()
    {
        using var client = CreateClient();
        var key = QueryKey.Create("todos");
        var first = (await client.FetchAsync(key, _ => Task.FromResult(new List<Todo> { new(1, "a", false) }))).Data;

        var second = (await client.FetchAsync(key, _ => Task.FromResult(new List<Todo> { new(1, "a", false) }), forceRefetch: true)).Data;

        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task Equal_refetch_keeps_the_cached_instance()
    {
        using var client = CreateClient();
        var key = QueryKey.Create("todos");
        var first = (await client.FetchAsync(key, _ => Task.FromResult(new List<Todo> { new(1, "a", false), new(2, "b", true) }), Sharing)).Data;

        var refetched = await client.FetchAsync(key, _ => Task.FromResult(new List<Todo> { new(1, "a", false), new(2, "b", true) }), Sharing, forceRefetch: true);

        Assert.Same(first, refetched.Data);
        Assert.Equal(QueryStatus.Success, refetched.Status);
    }

    [Fact]
    public async Task Changed_refetch_returns_new_list_that_reuses_unchanged_items()
    {
        using var client = CreateClient();
        var key = QueryKey.Create("todos");
        var first = (await client.FetchAsync(key, _ => Task.FromResult(new List<Todo> { new(1, "a", false), new(2, "b", false), new(3, "c", false) }), Sharing)).Data!;

        var second = (await client.FetchAsync(key, _ => Task.FromResult(new List<Todo> { new(1, "a", false), new(2, "b", true), new(3, "c", false) }), Sharing, forceRefetch: true)).Data!;

        Assert.NotSame(first, second);
        Assert.Same(first[0], second[0]);
        Assert.NotSame(first[1], second[1]);
        Assert.True(second[1].Done);
        Assert.Same(first[2], second[2]);
    }

    [Fact]
    public async Task Arrays_and_dictionaries_are_shared_element_wise()
    {
        using var client = CreateClient(sharingByDefault: true);
        var arrayKey = QueryKey.Create("array");
        var mapKey = QueryKey.Create("map");
        var a1 = (await client.FetchAsync(arrayKey, _ => Task.FromResult(new[] { new Todo(1, "a", false), new Todo(2, "b", false) }))).Data!;
        var m1 = (await client.FetchAsync(mapKey, _ => Task.FromResult(new Dictionary<string, Todo> { ["x"] = new(1, "a", false), ["y"] = new(2, "b", false) }))).Data!;

        var a2 = (await client.FetchAsync(arrayKey, _ => Task.FromResult(new[] { new Todo(1, "a", false), new Todo(2, "B", false) }), forceRefetch: true)).Data!;
        var m2 = (await client.FetchAsync(mapKey, _ => Task.FromResult(new Dictionary<string, Todo> { ["x"] = new(1, "a", false), ["y"] = new(2, "b", false) }), forceRefetch: true)).Data!;

        Assert.Same(a1[0], a2[0]);
        Assert.NotSame(a1[1], a2[1]);
        Assert.Same(m1, m2);
    }

    [Fact]
    public async Task Equal_records_and_strings_keep_the_cached_instance()
    {
        using var client = CreateClient(sharingByDefault: true);
        var todoKey = QueryKey.Create("todo");
        var textKey = QueryKey.Create("text");
        var todo = (await client.FetchAsync(todoKey, _ => Task.FromResult(new Todo(1, "a", false)))).Data;
        var text = (await client.FetchAsync(textKey, _ => Task.FromResult(new string('x', 3)))).Data;

        Assert.Same(todo, (await client.FetchAsync(todoKey, _ => Task.FromResult(new Todo(1, "a", false)), forceRefetch: true)).Data);
        Assert.Same(text, (await client.FetchAsync(textKey, _ => Task.FromResult(new string('x', 3)), forceRefetch: true)).Data);
    }

    [Fact]
    public async Task A_throwing_Equals_disables_sharing_without_failing_the_fetch()
    {
        using var client = CreateClient(sharingByDefault: true);
        var key = QueryKey.Create("broken");
        await client.FetchAsync(key, _ => Task.FromResult(new ThrowingEquals()));
        var replacement = new ThrowingEquals();

        var result = await client.FetchAsync(key, _ => Task.FromResult(replacement), forceRefetch: true);

        Assert.True(result.IsSuccess);
        Assert.Same(replacement, result.Data);
    }

    [Fact]
    public async Task Per_query_option_overrides_the_client_default()
    {
        using var client = CreateClient(sharingByDefault: true);
        var key = QueryKey.Create("opt-out");
        var off = new QueryOptions { StructuralSharing = false };
        var first = (await client.FetchAsync(key, _ => Task.FromResult(new List<int> { 1, 2 }), off)).Data;

        var second = (await client.FetchAsync(key, _ => Task.FromResult(new List<int> { 1, 2 }), off, forceRefetch: true)).Data;

        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task Equal_refetch_still_updates_the_timestamp_and_notifies()
    {
        var time = new FakeTimeProvider();
        using var client = new QueryClient(TestUtil.NoRetry(o => o.DefaultStructuralSharing = true), time);
        var key = QueryKey.Create("stamp");
        var first = await client.FetchAsync(key, _ => Task.FromResult(new List<int> { 1 }));
        using var observer = client.Watch<List<int>>(key);
        var notifications = 0;
        observer.StateChanged += _ => Interlocked.Increment(ref notifications);
        time.Advance(TimeSpan.FromSeconds(5));

        var second = await client.FetchAsync(key, _ => Task.FromResult(new List<int> { 1 }), forceRefetch: true);

        Assert.Same(first.Data, second.Data);
        Assert.True(second.DataUpdatedAt > first.DataUpdatedAt);
        await TestUtil.WaitUntilAsync(() => Volatile.Read(ref notifications) >= 2); // Refetching, then Success
    }

    [Fact]
    public void SetQueryData_with_equal_data_keeps_the_cached_instance_when_sharing()
    {
        using var client = CreateClient(sharingByDefault: true);
        var key = QueryKey.Create("written");
        var original = new List<Todo> { new(1, "a", false), new(2, "b", true) };
        client.SetQueryData(key, original);

        client.SetQueryData(key, new List<Todo> { new(1, "a", false), new(2, "b", true) });

        Assert.Same(original, client.GetQueryData<List<Todo>>(key).Data);
    }

    [Fact]
    public void Updaters_reuse_unchanged_elements_when_sharing()
    {
        using var client = CreateClient(sharingByDefault: true);
        var key = QueryKey.Create("updated");
        var first = new Todo(1, "a", false);
        client.SetQueryData(key, new List<Todo> { first, new(2, "b", false) });

        var updated = client.SetQueryData<List<Todo>>(key, list => list!.Select(t => t.Id == 2 ? t with { Done = true } : t with { }).ToList());

        Assert.Same(first, updated[0]); // an equal copy of item 1 was replaced by the cached instance
        Assert.True(updated[1].Done);
    }

    [Fact]
    public async Task Hydrated_and_fetched_queries_follow_their_own_sharing_setting()
    {
        using var client = CreateClient(sharingByDefault: false);
        var key = QueryKey.Create("per-query");
        var fetched = (await client.FetchAsync(key, _ => Task.FromResult(new List<int> { 1, 2 }), Sharing)).Data; // opted in

        client.HydrateQueryData(key, new List<int> { 1, 2 }, DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Same(fetched, client.GetQueryData<List<int>>(key).Data);

        var other = QueryKey.Create("default-off");
        var written = new List<int> { 1 };
        client.SetQueryData(other, written);
        client.SetQueryData(other, new List<int> { 1 });
        Assert.NotSame(written, client.GetQueryData<List<int>>(other).Data); // client default (off) applies before a fetch
    }

    [Fact]
    public void Equal_writes_do_not_notify_data_only_observers()
    {
        using var client = CreateClient(sharingByDefault: true);
        var key = QueryKey.Create("quiet-write");
        client.SetQueryData(key, new List<int> { 1, 2, 3 });
        using var observer = client.Watch<List<int>>(key, QueryStateProperties.Data);
        var notifications = 0;
        observer.StateChanged += _ => Interlocked.Increment(ref notifications);

        client.SetQueryData(key, new List<int> { 1, 2, 3 });
        client.SetQueryData(key, new List<int> { 1, 2, 4 });

        Assert.Equal(1, Volatile.Read(ref notifications)); // only the real change
    }
}
