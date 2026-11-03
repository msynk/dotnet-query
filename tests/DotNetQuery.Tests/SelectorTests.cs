using DotNetQuery;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Tests;

public class SelectorTests
{
    private sealed record Todo(int Id, string Title, bool Done);

    private static QueryClient CreateClient(bool sharing = false) =>
        new(TestUtil.NoRetry(o => o.DefaultStructuralSharing = sharing), new FakeTimeProvider());

    private static readonly QueryKey Key = QueryKey.Create("todos");

    private static Task<List<Todo>> Todos(params Todo[] todos) => Task.FromResult(todos.ToList());

    [Fact]
    public async Task Selector_projects_the_cached_data()
    {
        using var client = CreateClient();
        await client.FetchAsync(Key, _ => Todos(new Todo(1, "a", false), new Todo(2, "b", true)));

        using var open = client.Watch(Key, (List<Todo> todos) => todos.Count(t => !t.Done));

        Assert.Equal(1, open.CurrentState.Data);
        Assert.True(open.CurrentState.IsSuccess);
        Assert.Equal(client.GetQueryData<List<Todo>>(Key).DataUpdatedAt, open.CurrentState.DataUpdatedAt);
    }

    [Fact]
    public async Task Data_only_observer_ignores_status_flips_and_equal_refetches()
    {
        using var client = CreateClient();
        using var titles = client.Watch(Key, (List<Todo> todos) => todos.Select(t => t.Title).ToList(), QueryStateProperties.Data);
        var notifications = new List<List<string>?>();
        titles.StateChanged += s => { lock (notifications) { notifications.Add(s.Data); } };

        await client.FetchAsync(Key, _ => Todos(new Todo(1, "a", false)));
        await client.FetchAsync(Key, _ => Todos(new Todo(1, "a", true)), forceRefetch: true);   // titles unchanged
        await client.FetchAsync(Key, _ => Todos(new Todo(1, "z", true)), forceRefetch: true);   // titles changed

        lock (notifications)
        {
            Assert.Equal(2, notifications.Count);
            Assert.Equal(["a"], notifications[0]);
            Assert.Equal(["z"], notifications[1]);
        }

        // CurrentState is always current, even for suppressed changes.
        Assert.Equal(QueryStatus.Success, titles.CurrentState.Status);
        Assert.Equal(["z"], titles.CurrentState.Data);
    }

    [Fact]
    public async Task Default_notify_on_all_reports_every_transition()
    {
        using var client = CreateClient();
        using var count = client.Watch(Key, (List<Todo> todos) => todos.Count);
        var statuses = new List<QueryStatus>();
        count.StateChanged += s => { lock (statuses) { statuses.Add(s.Status); } };

        await client.FetchAsync(Key, _ => Todos(new Todo(1, "a", false)));

        lock (statuses)
        {
            Assert.Equal([QueryStatus.Loading, QueryStatus.Success], statuses);
        }
    }

    [Fact]
    public async Task Selector_runs_once_per_data_instance()
    {
        using var client = CreateClient(sharing: true);
        var runs = 0;
        using var count = client.Watch(Key, (List<Todo> todos) => { Interlocked.Increment(ref runs); return todos.Count; });

        await client.FetchAsync(Key, _ => Todos(new Todo(1, "a", false)));
        await client.FetchAsync(Key, _ => Todos(new Todo(1, "a", false)), forceRefetch: true); // shared: same instance

        Assert.Equal(1, Volatile.Read(ref runs));
    }

    [Fact]
    public async Task Throwing_selector_reports_an_error_and_recovers_on_new_data()
    {
        using var client = CreateClient();
        await client.FetchAsync(Key, _ => Todos(new Todo(1, "a", false)));
        using var first = client.Watch(Key, (List<Todo> todos) => todos[0].Title.Length == 0 ? throw new FormatException("empty title") : todos[0].Title);
        Assert.Equal("a", first.CurrentState.Data);

        await client.FetchAsync(Key, _ => Todos(new Todo(1, "", false)), forceRefetch: true);

        await TestUtil.WaitUntilAsync(() => first.CurrentState.IsError);
        Assert.IsType<FormatException>(first.CurrentState.Error);
        Assert.Equal("a", first.CurrentState.Data); // last good selection kept
        Assert.True(first.CurrentState.IsRefetchError);

        await client.FetchAsync(Key, _ => Todos(new Todo(1, "b", false)), forceRefetch: true);

        await TestUtil.WaitUntilAsync(() => first.CurrentState.IsSuccess);
        Assert.Equal("b", first.CurrentState.Data);
        Assert.Null(first.CurrentState.Error);
    }

    [Fact]
    public void Selector_failing_on_the_first_selection_is_a_loading_error()
    {
        using var client = CreateClient();
        client.SetQueryData(Key, new List<Todo>());

        using var observer = client.Watch(Key, (List<Todo> todos) => todos[0]);

        Assert.True(observer.CurrentState.IsLoadingError);
        Assert.IsType<ArgumentOutOfRangeException>(observer.CurrentState.Error);
    }

    [Fact]
    public async Task Disposing_a_selection_observer_unsubscribes_it()
    {
        using var client = CreateClient();
        var observer = client.Watch(Key, (List<Todo> todos) => todos.Count);
        Assert.Equal(1, client.Cache.Describe(Key)!.ObserverCount);
        var notified = false;
        observer.StateChanged += _ => notified = true;

        observer.Dispose();
        await client.FetchAsync(Key, _ => Todos(new Todo(1, "a", false)));

        Assert.Equal(0, client.Cache.Describe(Key)!.ObserverCount);
        Assert.False(notified);
    }

    [Fact]
    public async Task Notify_filter_without_selector()
    {
        using var client = CreateClient();
        using var errors = client.Watch<List<Todo>>(Key, QueryStateProperties.Error);
        var notifications = 0;
        errors.StateChanged += _ => Interlocked.Increment(ref notifications);

        await client.FetchAsync(Key, _ => Todos(new Todo(1, "a", false)));
        Assert.Equal(0, Volatile.Read(ref notifications));

        await client.FetchAsync<List<Todo>>(Key, _ => throw new InvalidOperationException("down"), forceRefetch: true);

        await TestUtil.WaitUntilAsync(() => Volatile.Read(ref notifications) == 1);
        Assert.True(errors.CurrentState.IsRefetchError);
    }

    [Fact]
    public async Task Throwing_comparer_never_blocks_other_observers()
    {
        using var client = CreateClient();
        using var broken = client.Watch(Key, (List<Todo> todos) => todos.Count, QueryStateProperties.Data, new ThrowingComparer());
        using var plain = client.Watch<List<Todo>>(Key);
        var plainNotifications = 0;
        plain.StateChanged += _ => Interlocked.Increment(ref plainNotifications);

        await client.FetchAsync(Key, _ => Todos(new Todo(1, "a", false)));
        await client.FetchAsync(Key, _ => Todos(new Todo(1, "a", false), new Todo(2, "b", false)), forceRefetch: true);

        await TestUtil.WaitUntilAsync(() => Volatile.Read(ref plainNotifications) >= 4);
        Assert.Equal(2, broken.CurrentState.Data);
    }

    [Fact]
    public async Task Many_concurrent_selection_observers_converge_on_the_final_state()
    {
        using var client = CreateClient();
        var observers = Enumerable.Range(0, 200)
            .Select(i => client.Watch(Key, (List<Todo> todos) => todos.Count + i, i % 2 == 0 ? QueryStateProperties.All : QueryStateProperties.Data))
            .ToList();

        await Task.WhenAll(Enumerable.Range(1, 20).Select(n =>
            Task.Run(() => client.SetQueryData<List<Todo>>(Key, current => [.. current ?? [], new Todo(n, "t", false)]))));

        for (var i = 0; i < observers.Count; i++)
        {
            var expected = 20 + i;
            var observer = observers[i];
            await TestUtil.WaitUntilAsync(() => observer.CurrentState.Data == expected);
        }

        observers.ForEach(o => o.Dispose());
        Assert.Equal(0, client.Cache.Describe(Key)!.ObserverCount);
    }

    private sealed class ThrowingComparer : IEqualityComparer<int>
    {
        public bool Equals(int x, int y) => throw new InvalidOperationException("comparer bug");

        public int GetHashCode(int obj) => obj;
    }
}
