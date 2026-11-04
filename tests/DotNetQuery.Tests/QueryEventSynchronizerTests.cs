using System.Collections.Immutable;
using DotNetQuery;
using DotNetQuery.Synchronization;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Tests;

public sealed class QueryEventSynchronizerTests : IAsyncLifetime
{
    private sealed record Todo(int Id, string Title, long Version = 1);

    private sealed record TodoDeleted(int Id);

    private sealed record Paged(IReadOnlyList<Todo> Items, int Total);

    private sealed record Envelope(string? Id, int TodoId, long Version, string Title);

    private static readonly QueryKey Todos = QueryKey.Create("todos");
    private static readonly QueryKey TodoList = Todos.Append("list");
    private static readonly QueryKey TodoStats = QueryKey.Create("stats");

    private static readonly QueryEntity<Todo, int> TodoEntity = QueryEntity.Create<Todo, int>(Todos, t => t.Id, version: t => t.Version);

    private readonly List<IAsyncDisposable> _disposables = [];
    private readonly List<IDisposable> _clients = [];

    private static QueryKey TodoById(int id) => Todos.Append("by-id", id);

    private QueryClient NewClient(TimeProvider? time = null)
    {
        var client = new QueryClient(TestUtil.NoRetry(o => o.DefaultStaleTime = TimeSpan.FromMinutes(5)), time);
        _clients.Add(client);
        return client;
    }

    private QueryEventSynchronizer NewSync(IQueryClient client, IQueryEventSource? source = null, QueryEventSyncOptions? options = null)
    {
        var sync = new QueryEventSynchronizer(client, source, options);
        _disposables.Add(sync);
        return sync;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            await disposable.DisposeAsync();
        }

        _clients.ForEach(c => c.Dispose());
    }

    private static Func<CancellationToken, Task<T>> Counting<T>(Func<T> value, Action onFetch) => _ =>
    {
        onFetch();
        return Task.FromResult(value());
    };

    // ------------------------------------------------------------------ mapping strategies

    [Fact]
    public async Task Invalidate_marks_matching_queries_stale_and_refetches_observed_ones()
    {
        var client = NewClient();
        var fetches = 0;
        using var observer = client.Watch<List<Todo>>(TodoList);
        await client.FetchAsync(TodoList, Counting(() => new List<Todo>(), () => Interlocked.Increment(ref fetches)));
        await client.FetchAsync(TodoStats, _ => Task.FromResult(1));

        var sync = NewSync(client);
        sync.Map<TodoDeleted>("TodoDeleted", m => m.Invalidate(Todos));

        Assert.Equal(QueryEventOutcome.Applied, await sync.ProcessAsync("TodoDeleted", new TodoDeleted(1)));
        await TestUtil.WaitUntilAsync(() => Volatile.Read(ref fetches) == 2);
        Assert.False(client.Cache.Describe(TodoStats)!.IsStale); // outside the prefix
    }

    [Fact]
    public async Task Invalidate_with_a_filter_computed_from_the_event_targets_exactly()
    {
        var client = NewClient();
        await client.FetchAsync(TodoById(1), _ => Task.FromResult(new Todo(1, "a")));
        await client.FetchAsync(TodoById(2), _ => Task.FromResult(new Todo(2, "b")));
        var sync = NewSync(client);
        sync.Map<TodoDeleted>("TodoDeleted", m => m.Invalidate(e => QueryFilter.ForKey(TodoById(e.Id), exact: true)).Covers(Todos));

        await sync.ProcessAsync("TodoDeleted", new TodoDeleted(2));

        Assert.False(client.Cache.Describe(TodoById(1))!.IsStale);
        Assert.True(client.Cache.Describe(TodoById(2))!.IsStale);
    }

    [Fact]
    public async Task SetData_writes_only_cached_queries_unless_asked_to_create()
    {
        var client = NewClient();
        var sync = NewSync(client);
        sync.Map<Todo>("TodoChanged", m => m.SetData(t => TodoById(t.Id), t => t).Covers(Todos));
        sync.Map<Todo>("TodoCreated", m => m.SetData(t => TodoById(t.Id), t => t, createIfMissing: true).Covers(Todos));

        await sync.ProcessAsync("TodoChanged", new Todo(1, "a"));
        Assert.Null(client.Cache.Describe(TodoById(1))); // no incomplete entry was created

        client.SetQueryData(TodoById(1), new Todo(1, "old"));
        await sync.ProcessAsync("TodoChanged", new Todo(1, "new"));
        Assert.Equal("new", client.GetQueryData<Todo>(TodoById(1)).Data!.Title);

        await sync.ProcessAsync("TodoCreated", new Todo(2, "created"));
        Assert.Equal("created", client.GetQueryData<Todo>(TodoById(2)).Data!.Title);
    }

    [Fact]
    public async Task Update_transforms_every_matching_query_that_holds_data_and_skips_unchanged_ones()
    {
        var client = NewClient();
        var sync = NewSync(client);
        client.SetQueryData(TodoList.Append("open"), new List<Todo> { new(1, "a"), new(2, "b") });
        client.SetQueryData(TodoList.Append("all"), new List<Todo> { new(3, "c") });
        await client.FetchAsync(TodoList.Append("empty"), _ => Task.FromException<List<Todo>>(new InvalidOperationException("boom")));
        var notifications = 0;
        using var observer = client.Watch<List<Todo>>(TodoList.Append("all"));
        observer.StateChanged += _ => Interlocked.Increment(ref notifications);
        var before = Volatile.Read(ref notifications);

        sync.Map<Todo>("Renamed", m => m.Update<List<Todo>>(TodoList, (list, e) =>
            list.Any(t => t.Id == e.Id) ? [.. list.Select(t => t.Id == e.Id ? e : t)] : list));

        Assert.Equal(QueryEventOutcome.Applied, await sync.ProcessAsync("Renamed", new Todo(2, "B")));

        Assert.Equal(["a", "B"], client.GetQueryData<List<Todo>>(TodoList.Append("open")).Data!.Select(t => t.Title));
        Assert.Equal(before, Volatile.Read(ref notifications)); // returned unchanged: not written, not re-rendered
        Assert.False(client.GetQueryData<List<Todo>>(TodoList.Append("empty")).HasData); // never created data
    }

    [Fact]
    public async Task Upsert_replaces_details_and_list_items_and_never_overwrites_a_newer_version()
    {
        var client = NewClient();
        var sync = NewSync(client);
        client.SetQueryData(TodoList, new List<Todo> { new(1, "a", 1), new(2, "b", 1) });
        client.SetQueryData(TodoById(1), new Todo(1, "a", 1));
        client.SetQueryData(TodoById(2), new Todo(2, "b", 1));
        sync.Map<Todo>("TodoChanged", m => m.Upsert(TodoEntity, t => t));

        await sync.ProcessAsync("TodoChanged", new Todo(1, "A", 3));
        Assert.Equal("A", client.GetQueryData<Todo>(TodoById(1)).Data!.Title);
        Assert.Equal("b", client.GetQueryData<Todo>(TodoById(2)).Data!.Title);
        Assert.Equal(["A", "b"], client.GetQueryData<List<Todo>>(TodoList).Data!.Select(t => t.Title));

        // An older snapshot of the same entity (a late event, whoever wrote the newer one) is ignored.
        await sync.ProcessAsync("TodoChanged", new Todo(1, "old", 2));
        Assert.Equal("A", client.GetQueryData<Todo>(TodoById(1)).Data!.Title);
        Assert.Equal("A", client.GetQueryData<List<Todo>>(TodoList).Data![0].Title);
    }

    [Fact]
    public async Task Upsert_of_an_entity_missing_from_a_list_follows_the_missing_behavior()
    {
        var client = NewClient();
        var sync = NewSync(client);
        await client.FetchAsync(TodoList, _ => Task.FromResult(new List<Todo> { new(1, "a") }));
        client.SetQueryData(QueryKey.Create("todos", "feed"), new List<Todo> { new(1, "a") });
        client.SetQueryData(QueryKey.Create("todos", "archive"), new List<Todo> { new(1, "a") });
        var feed = QueryEntity.Create<Todo, int>(QueryKey.Create("todos", "feed"), t => t.Id);
        var archive = QueryEntity.Create<Todo, int>(QueryKey.Create("todos", "archive"), t => t.Id);
        sync.Map<Todo>("Created", m => m.Upsert(TodoEntity.WithCollection<Paged>(p => p.Items, (p, items) => p with { Items = items }), t => t));
        sync.Map<Todo>("Pushed", m => m.Upsert(feed, t => t, EntityMissingBehavior.Prepend));
        sync.Map<Todo>("Archived", m => m.Upsert(archive, t => t, EntityMissingBehavior.Ignore));

        await sync.ProcessAsync("Created", new Todo(5, "e"));
        Assert.True(client.Cache.Describe(TodoList)!.IsStale); // membership unknown: refetched instead
        Assert.Single(client.GetQueryData<List<Todo>>(TodoList).Data!);

        await sync.ProcessAsync("Pushed", new Todo(6, "f"));
        Assert.Equal([6, 1], client.GetQueryData<List<Todo>>(QueryKey.Create("todos", "feed")).Data!.Select(t => t.Id));

        await sync.ProcessAsync("Archived", new Todo(7, "g"));
        Assert.Equal([1], client.GetQueryData<List<Todo>>(QueryKey.Create("todos", "archive")).Data!.Select(t => t.Id));
    }

    [Fact]
    public async Task Upsert_recognizes_every_built_in_collection_shape()
    {
        var client = NewClient();
        var sync = NewSync(client);
        client.SetQueryData<IReadOnlyList<Todo>>(Todos.Append("ro"), [new(1, "a")]);
        client.SetQueryData(Todos.Append("array"), new[] { new Todo(1, "a") });
        client.SetQueryData(Todos.Append("immutable"), ImmutableArray.Create(new Todo(1, "a")));
        client.SetQueryData(Todos.Append("immutable-list"), ImmutableList.Create(new Todo(1, "a")));
        sync.Map<Todo>("TodoChanged", m => m.Upsert(TodoEntity, t => t));

        await sync.ProcessAsync("TodoChanged", new Todo(1, "A", 2));

        Assert.Equal("A", client.GetQueryData<IReadOnlyList<Todo>>(Todos.Append("ro")).Data![0].Title);
        Assert.Equal("A", client.GetQueryData<Todo[]>(Todos.Append("array")).Data![0].Title);
        Assert.Equal("A", client.GetQueryData<ImmutableArray<Todo>>(Todos.Append("immutable")).Data[0].Title);
        Assert.Equal("A", client.GetQueryData<ImmutableList<Todo>>(Todos.Append("immutable-list")).Data![0].Title);
    }

    [Fact]
    public async Task Remove_takes_the_entity_out_of_lists_and_handles_details()
    {
        var client = NewClient();
        var sync = NewSync(client);
        await client.FetchAsync(TodoById(1), _ => Task.FromResult(new Todo(1, "a")));
        client.SetQueryData(TodoById(2), new Todo(2, "b"));
        client.SetQueryData(TodoList, new List<Todo> { new(1, "a"), new(2, "b") });
        sync.Map<TodoDeleted>("Deleted", m => m.Remove(TodoEntity, e => e.Id));
        sync.Map<TodoDeleted>("Purged", m => m.Remove(TodoEntity, e => e.Id, EntityDetailRemoval.Remove));

        await sync.ProcessAsync("Deleted", new TodoDeleted(1));
        Assert.Equal([2], client.GetQueryData<List<Todo>>(TodoList).Data!.Select(t => t.Id));
        Assert.True(client.Cache.Describe(TodoById(1))!.IsStale);

        await sync.ProcessAsync("Purged", new TodoDeleted(2));
        Assert.Empty(client.GetQueryData<List<Todo>>(TodoList).Data!);
        Assert.Null(client.Cache.Describe(TodoById(2)));
    }

    [Fact]
    public async Task Paged_collections_refetch_when_membership_changes_but_not_on_in_place_updates()
    {
        var client = NewClient();
        var sync = NewSync(client);
        var key = Todos.Append("page", 1);
        await client.FetchAsync(key, _ => Task.FromResult(new Paged([new(1, "a"), new(2, "b")], 10)));
        var paged = TodoEntity.WithCollection<Paged>(p => p.Items, (p, items) => p with { Items = items }, refetchOnMembershipChange: true);
        sync.Map<Todo>("Changed", m => m.Upsert(paged, t => t));
        sync.Map<TodoDeleted>("Deleted", m => m.Remove(paged, e => e.Id));

        await sync.ProcessAsync("Changed", new Todo(2, "B", 2));
        Assert.Equal("B", client.GetQueryData<Paged>(key).Data!.Items[1].Title);
        Assert.False(client.Cache.Describe(key)!.IsStale);

        await sync.ProcessAsync("Deleted", new TodoDeleted(1));
        Assert.Single(client.GetQueryData<Paged>(key).Data!.Items);
        Assert.True(client.Cache.Describe(key)!.IsStale); // totals and page boundaries moved
    }

    [Fact]
    public async Task Infinite_queries_are_updated_page_by_page()
    {
        var client = NewClient();
        var sync = NewSync(client);
        var key = Todos.Append("infinite");
        client.SetQueryData(key, new InfiniteData<int, List<Todo>>
        {
            Pages = [[new(1, "a"), new(2, "b")], [new(3, "c")]],
            PageParams = [0, 1],
        });
        var cursorPaged = TodoEntity.WithInfinitePages<int, List<Todo>>(page => page, (_, items) => [.. items], refetchOnMembershipChange: false);
        sync.Map<Todo>("Changed", m => m.Upsert(cursorPaged, t => t, EntityMissingBehavior.Prepend));
        sync.Map<TodoDeleted>("Deleted", m => m.Remove(cursorPaged, e => e.Id));

        await sync.ProcessAsync("Changed", new Todo(3, "C", 2));
        await sync.ProcessAsync("Changed", new Todo(9, "new"));
        await sync.ProcessAsync("Deleted", new TodoDeleted(2));

        var pages = client.GetQueryData<InfiniteData<int, List<Todo>>>(key).Data!.Pages;
        Assert.Equal([9, 1], pages[0].Select(t => t.Id));
        Assert.Equal("C", pages[1][0].Title);
        Assert.Equal([0, 1], client.GetQueryData<InfiniteData<int, List<Todo>>>(key).Data!.PageParams);
    }

    [Fact]
    public async Task One_event_updates_several_queries_atomically()
    {
        var client = NewClient();
        var sync = NewSync(client);
        client.SetQueryData(TodoList, new List<Todo> { new(1, "a") });
        client.SetQueryData(TodoById(1), new Todo(1, "a"));
        var torn = 0;
        using var observer = client.Watch<Todo>(TodoById(1));
        observer.StateChanged += state =>
        {
            // When the detail changes, the list must already show the same title.
            if (state.Data?.Title == "A" && client.GetQueryData<List<Todo>>(TodoList).Data![0].Title != "A")
            {
                Interlocked.Increment(ref torn);
            }
        };
        sync.Map<Todo>("Changed", m => m.Upsert(TodoEntity, t => t));

        await sync.ProcessAsync("Changed", new Todo(1, "A", 2));

        Assert.Equal(0, torn);
        Assert.Equal("A", client.GetQueryData<Todo>(TodoById(1)).Data!.Title);
    }

    [Fact]
    public async Task A_failing_mapping_invalidates_its_targets_and_does_not_stop_other_mappings()
    {
        var client = NewClient();
        var sync = NewSync(client);
        await client.FetchAsync(TodoList, _ => Task.FromResult(new List<Todo> { new(1, "a") }));
        client.SetQueryData(TodoStats, 1);
        sync.Map<Todo>("Changed", m => m.Update<List<Todo>>(TodoList, (_, _) => throw new InvalidOperationException("bad shape")));
        sync.Map<Todo>("Changed", m => m.Update<int>(TodoStats, (count, _) => count + 1));

        var outcome = await sync.ProcessAsync("Changed", new Todo(1, "A"));

        Assert.Equal(QueryEventOutcome.Failed, outcome);
        Assert.Equal("a", client.GetQueryData<List<Todo>>(TodoList).Data![0].Title); // nothing half-written
        Assert.True(client.Cache.Describe(TodoList)!.IsStale);                     // reconciled by refetch instead
        Assert.Equal(2, client.GetQueryData<int>(TodoStats).Data);                  // the other mapping applied
        var stats = sync.GetStatistics();
        Assert.Equal(1, stats.Failed);
        Assert.Contains(stats.RecentErrors, e => e.EventName == "Changed" && e.Message == "bad shape");
    }

    [Fact]
    public async Task Handlers_run_after_data_operations_and_can_invalidate()
    {
        var client = NewClient();
        var sync = NewSync(client);
        await client.FetchAsync(TodoStats, _ => Task.FromResult(0));
        client.SetQueryData(TodoById(1), new Todo(1, "a"));
        string? seen = null;
        sync.Map<Todo>("Changed", m => m
            .Upsert(TodoEntity, t => t)
            .Handle(context =>
            {
                seen = context.Client.GetQueryData<Todo>(TodoById(1)).Data!.Title;
                context.Invalidate(TodoStats);
            })
            .Covers(TodoStats));

        await sync.ProcessAsync("Changed", new Todo(1, "A", 2));

        Assert.Equal("A", seen);
        Assert.True(client.Cache.Describe(TodoStats)!.IsStale);
    }

    [Fact]
    public async Task Where_filters_events_and_unknown_events_are_reported()
    {
        var client = NewClient();
        var sync = NewSync(client);
        client.SetQueryData(TodoById(1), new Todo(1, "a"));
        sync.Map<Todo>("Changed", m => m.Where(t => t.Id != 1).Upsert(TodoEntity, t => t));

        Assert.Equal(QueryEventOutcome.Filtered, await sync.ProcessAsync("Changed", new Todo(1, "A", 2)));
        Assert.Equal(QueryEventOutcome.NoMapping, await sync.ProcessAsync("Unknown", 1));
        Assert.Equal("a", client.GetQueryData<Todo>(TodoById(1)).Data!.Title);
    }

    [Fact]
    public void Registration_validates_mappings()
    {
        var sync = NewSync(NewClient());
        Assert.Throws<InvalidOperationException>(() => sync.Map<Todo>("Changed", _ => { }));
        Assert.Throws<InvalidOperationException>(() => sync.Map<Todo>("Changed", m => m.SetData(t => TodoById(t.Id), t => t)));
        sync.Map<Todo>("Changed", m => m.Upsert(TodoEntity, t => t));
        Assert.Throws<InvalidOperationException>(() => sync.Map<int>("Changed", m => m.Invalidate(Todos)));
        sync.Map<Todo>("Toast", m => m.Handle(_ => { }).NoRecovery());
        sync.Map<Todo>("Changed", m => m.WithMetadata(t => new QueryEventMetadata { EventId = t.Id.ToString() }).Invalidate(Todos));
        Assert.Throws<InvalidOperationException>(() =>
            sync.Map<Todo>("Changed", m => m.WithMetadata(t => default).Invalidate(Todos)));
    }

    // ------------------------------------------------------------------ duplicates and ordering

    [Fact]
    public async Task Duplicate_event_ids_are_applied_once_even_when_delivered_concurrently()
    {
        var client = NewClient();
        var sync = NewSync(client);
        client.SetQueryData(TodoStats, 0);
        sync.Map<Envelope>("Counted", m => m
            .WithMetadata(e => new QueryEventMetadata { EventId = e.Id })
            .Update<int>(TodoStats, (count, _) => count + 1));

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ =>
            Task.Run(() => sync.ProcessAsync("Counted", new Envelope("evt-1", 1, 1, "x")).AsTask())));

        Assert.Equal(1, client.GetQueryData<int>(TodoStats).Data);
        Assert.Equal(1, outcomes.Count(o => o == QueryEventOutcome.Applied));
        Assert.Equal(49, outcomes.Count(o => o == QueryEventOutcome.Duplicate));
        Assert.Equal(49, sync.GetStatistics().Duplicates);
    }

    [Fact]
    public async Task Duplicate_detection_expires_and_is_bounded()
    {
        var time = new FakeTimeProvider();
        var client = NewClient(time);
        var sync = NewSync(client, options: new QueryEventSyncOptions { DeduplicationWindow = TimeSpan.FromMinutes(1), DeduplicationCapacity = 2 });
        client.SetQueryData(TodoStats, 0);
        sync.Map<Envelope>("Counted", m => m
            .WithMetadata(e => new QueryEventMetadata { EventId = e.Id })
            .Update<int>(TodoStats, (count, _) => count + 1));

        await sync.ProcessAsync("Counted", new Envelope("a", 1, 1, "x"));
        Assert.Equal(QueryEventOutcome.Duplicate, await sync.ProcessAsync("Counted", new Envelope("a", 1, 1, "x")));

        time.Advance(TimeSpan.FromMinutes(2)); // past the window
        Assert.Equal(QueryEventOutcome.Applied, await sync.ProcessAsync("Counted", new Envelope("a", 1, 1, "x")));

        await sync.ProcessAsync("Counted", new Envelope("b", 1, 1, "x"));
        await sync.ProcessAsync("Counted", new Envelope("c", 1, 1, "x")); // evicts "a" (capacity 2)
        Assert.Equal(QueryEventOutcome.Applied, await sync.ProcessAsync("Counted", new Envelope("a", 1, 1, "x")));
        Assert.Equal(5, client.GetQueryData<int>(TodoStats).Data);
    }

    [Fact]
    public async Task Events_without_an_id_can_be_required_to_have_one()
    {
        var client = NewClient();
        var sync = NewSync(client, options: new QueryEventSyncOptions { RequireEventId = true });
        sync.Map<Envelope>("Changed", m => m.WithMetadata(e => new QueryEventMetadata { EventId = e.Id }).Invalidate(Todos));

        Assert.Equal(QueryEventOutcome.Rejected, await sync.ProcessAsync("Changed", new Envelope(null, 1, 1, "x")));
        Assert.Equal(QueryEventOutcome.Applied, await sync.ProcessAsync("Changed", new Envelope("id", 1, 1, "x")));
    }

    [Fact]
    public async Task Malformed_metadata_is_rejected_without_breaking_processing()
    {
        var client = NewClient();
        var sync = NewSync(client);
        sync.Map<Envelope>("Changed", m => m
            .WithMetadata(e => e.Id is null ? throw new FormatException("no id") : new QueryEventMetadata { EventId = e.Id })
            .Invalidate(Todos));

        Assert.Equal(QueryEventOutcome.Rejected, await sync.ProcessAsync("Changed", new Envelope(null, 1, 1, "x")));
        Assert.Equal(QueryEventOutcome.Applied, await sync.ProcessAsync("Changed", new Envelope("ok", 1, 1, "x")));
        Assert.Equal(1, sync.GetStatistics().Rejected);
    }

    [Fact]
    public async Task Older_entity_versions_are_rejected_as_stale()
    {
        var client = NewClient();
        var sync = NewSync(client);
        client.SetQueryData(TodoById(1), new Todo(1, "a", 0));
        var plain = QueryEntity.Create<Todo, int>(Todos, t => t.Id); // no data-level version: the event version decides
        sync.Map<Envelope>("Changed", m => m
            .WithMetadata(e => new QueryEventMetadata { EntityId = e.TodoId.ToString(), Version = e.Version })
            .Upsert(plain, e => new Todo(e.TodoId, e.Title, e.Version)));

        Assert.Equal(QueryEventOutcome.Applied, await sync.ProcessAsync("Changed", new Envelope(null, 1, 5, "five")));
        Assert.Equal(QueryEventOutcome.Stale, await sync.ProcessAsync("Changed", new Envelope(null, 1, 4, "four")));
        Assert.Equal(QueryEventOutcome.Stale, await sync.ProcessAsync("Changed", new Envelope(null, 1, 5, "five again")));
        Assert.Equal(QueryEventOutcome.Applied, await sync.ProcessAsync("Changed", new Envelope(null, 2, 1, "other entity")));

        Assert.Equal("five", client.GetQueryData<Todo>(TodoById(1)).Data!.Title);
    }

    [Fact]
    public async Task Sequences_discard_stale_events()
    {
        var client = NewClient();
        var sync = NewSync(client);
        sync.Map<long>("Tick", m => m.WithMetadata(s => new QueryEventMetadata { Sequence = s }).Invalidate(Todos));

        Assert.Equal(QueryEventOutcome.Applied, await sync.ProcessAsync("Tick", 10L));
        Assert.Equal(QueryEventOutcome.Applied, await sync.ProcessAsync("Tick", 15L)); // jumps are fine without ContiguousSequences
        Assert.Equal(QueryEventOutcome.Stale, await sync.ProcessAsync("Tick", 12L));
        Assert.Equal(15, sync.GetCheckpoint());
    }

    [Fact]
    public async Task A_sequence_gap_holds_the_event_back_and_recovers_by_invalidation()
    {
        var client = NewClient();
        await client.FetchAsync(TodoList, _ => Task.FromResult(new List<Todo>()));
        var sync = NewSync(client, options: new QueryEventSyncOptions { ContiguousSequences = true });
        var applied = new List<long>();
        sync.Map<long>("Tick", m => m
            .WithMetadata(s => new QueryEventMetadata { Sequence = s })
            .Handle(context => applied.Add(context.Event))
            .Covers(Todos));

        await sync.ProcessAsync("Tick", 1L);
        await sync.ProcessAsync("Tick", 2L);
        Assert.Equal(QueryEventOutcome.Gap, await sync.ProcessAsync("Tick", 5L));
        await sync.ResynchronizeAsync(); // waits for the recovery the gap scheduled (coalesced)

        Assert.Equal([1L, 2L], applied);
        Assert.True(client.Cache.Describe(TodoList)!.IsStale);
        Assert.Equal(5, sync.GetCheckpoint()); // the invalidation covered everything seen
        Assert.Equal(QueryEventOutcome.Applied, await sync.ProcessAsync("Tick", 6L));
        Assert.Equal(QuerySyncStatus.Synchronized, sync.Status);
        Assert.Equal(1, sync.GetStatistics().Gaps);
    }

    [Fact]
    public async Task Replay_recovery_fills_a_gap_from_the_checkpoint()
    {
        var client = NewClient();
        var applied = new List<long>();
        long? replayedFrom = null;
        var history = new long[] { 1, 2, 3, 4, 5 };
        var sync = NewSync(client, options: new QueryEventSyncOptions
        {
            ContiguousSequences = true,
            Recovery = QuerySyncRecovery.Replay(async (context, ct) =>
            {
                // The gap's recovery replays from 2; a manual resync coalesced after it would start from 5.
                var checkpoint = context.GetCheckpoint();
                replayedFrom ??= checkpoint;
                foreach (var sequence in history.Where(s => s > (checkpoint ?? 0)))
                {
                    await context.ApplyAsync("Tick", sequence, ct);
                }

                return true;
            }),
        });
        sync.Map<long>("Tick", m => m.WithMetadata(s => new QueryEventMetadata { Sequence = s }).Handle(c => applied.Add(c.Event)).Covers(Todos));

        await sync.ProcessAsync("Tick", 1L);
        await sync.ProcessAsync("Tick", 2L);
        await sync.ProcessAsync("Tick", 5L); // gap: 3 and 4 were missed
        await sync.ResynchronizeAsync();

        Assert.Equal(2, replayedFrom);
        Assert.Equal([1L, 2L, 3L, 4L, 5L], applied);
        Assert.Equal(0, sync.GetStatistics().RecoveryFallbacks);
    }

    [Fact]
    public async Task Replay_without_history_falls_back_to_invalidation()
    {
        var client = NewClient();
        await client.FetchAsync(TodoList, _ => Task.FromResult(new List<Todo>()));
        var sync = NewSync(client, options: new QueryEventSyncOptions
        {
            ContiguousSequences = true,
            Recovery = QuerySyncRecovery.Replay((_, _) => Task.FromResult(false)),
        });
        sync.Map<long>("Tick", m => m.WithMetadata(s => new QueryEventMetadata { Sequence = s }).Invalidate(TodoStats).Covers(Todos));

        await sync.ProcessAsync("Tick", 1L);
        await sync.ProcessAsync("Tick", 3L);
        await sync.ResynchronizeAsync();

        Assert.True(client.Cache.Describe(TodoList)!.IsStale);
        Assert.InRange(sync.GetStatistics().RecoveryFallbacks, 1, 2); // the gap's recovery (and the manual one, if not coalesced)
    }

    [Fact]
    public async Task A_throwing_recovery_falls_back_to_invalidation()
    {
        var client = NewClient();
        await client.FetchAsync(TodoList, _ => Task.FromResult(new List<Todo>()));
        var sync = NewSync(client, options: new QueryEventSyncOptions
        {
            Recovery = QuerySyncRecovery.Snapshot((_, _) => throw new HttpRequestException("snapshot down")),
        });
        sync.Map<Todo>("Changed", m => m.Upsert(TodoEntity, t => t));

        await sync.ResynchronizeAsync();

        Assert.True(client.Cache.Describe(TodoList)!.IsStale);
        Assert.Equal(QuerySyncStatus.Synchronized, sync.Status);
        Assert.Contains(sync.GetStatistics().RecentErrors, e => e.Message == "snapshot down");
    }

    [Fact]
    public async Task Snapshot_recovery_with_a_watermark_discards_older_events()
    {
        var client = NewClient();
        client.SetQueryData(TodoStats, 0);
        var sync = NewSync(client, options: new QueryEventSyncOptions
        {
            Recovery = QuerySyncRecovery.Snapshot((context, _) =>
            {
                context.Client.SetQueryData(TodoStats, 100);
                context.SetCheckpoint(50);
                return Task.CompletedTask;
            }),
        });
        sync.Map<long>("Tick", m => m.WithMetadata(s => new QueryEventMetadata { Sequence = s }).Update<int>(TodoStats, (n, _) => n + 1));

        await sync.ResynchronizeAsync();
        Assert.Equal(QueryEventOutcome.Stale, await sync.ProcessAsync("Tick", 40L));
        Assert.Equal(QueryEventOutcome.Applied, await sync.ProcessAsync("Tick", 51L));
        Assert.Equal(101, client.GetQueryData<int>(TodoStats).Data);
    }

    [Fact]
    public async Task Events_of_another_scope_are_ignored_and_changing_scope_resets_ordering()
    {
        var client = NewClient();
        client.SetQueryData(TodoStats, 0);
        var sync = NewSync(client, options: new QueryEventSyncOptions { Scope = "tenant-a" });
        sync.Map<Envelope>("Counted", m => m
            .WithMetadata(e => new QueryEventMetadata { Scope = e.Id, Sequence = e.Version })
            .Update<int>(TodoStats, (n, _) => n + 1));

        Assert.Equal(QueryEventOutcome.Applied, await sync.ProcessAsync("Counted", new Envelope("tenant-a", 1, 10, "x")));
        Assert.Equal(QueryEventOutcome.OutOfScope, await sync.ProcessAsync("Counted", new Envelope("tenant-b", 1, 11, "x")));
        Assert.Equal(QueryEventOutcome.Applied, await sync.ProcessAsync("Counted", new Envelope(null, 1, 12, "x"))); // unscoped: global

        sync.ChangeScope("tenant-b");
        Assert.Equal("tenant-b", sync.Scope);
        Assert.Equal(QueryEventOutcome.Applied, await sync.ProcessAsync("Counted", new Envelope("tenant-b", 1, 1, "x"))); // new stream
        Assert.Equal(3, client.GetQueryData<int>(TodoStats).Data);
    }

    [Fact]
    public async Task An_event_stale_by_version_still_consumes_its_sequence_position()
    {
        var client = NewClient();
        var sync = NewSync(client, options: new QueryEventSyncOptions { ContiguousSequences = true });
        sync.Map<Envelope>("Changed", m => m
            .WithMetadata(e => new QueryEventMetadata { EntityId = e.TodoId.ToString(), Version = e.Version, Sequence = long.Parse(e.Id!) })
            .Invalidate(Todos));

        Assert.Equal(QueryEventOutcome.Applied, await sync.ProcessAsync("Changed", new Envelope("1", 1, 5, "x")));
        Assert.Equal(QueryEventOutcome.Stale, await sync.ProcessAsync("Changed", new Envelope("2", 1, 4, "older version, next position")));
        Assert.Equal(QueryEventOutcome.Applied, await sync.ProcessAsync("Changed", new Envelope("3", 2, 1, "x"))); // not a gap
        Assert.Equal(0, sync.GetStatistics().Gaps);
    }

    [Fact]
    public async Task Changing_scope_while_an_event_of_the_old_scope_is_processed_does_not_leak_its_ordering_state()
    {
        var client = NewClient();
        var sync = NewSync(client, options: new QueryEventSyncOptions { Scope = "a" });
        var inHandler = TestUtil.NewSignal();
        var release = TestUtil.NewSignal();
        sync.Map<Envelope>("Changed", m => m
            .WithMetadata(e => new QueryEventMetadata { Scope = e.Id, Sequence = e.Version })
            .Handle(async (context, ct) =>
            {
                if (context.Metadata.Sequence == 500)
                {
                    inHandler.TrySetResult();
                    await release.Task.WaitAsync(ct);
                }
            })
            .NoRecovery());

        var old = sync.ProcessAsync("Changed", new Envelope("a", 1, 500, "old scope")).AsTask();
        await inHandler.Task;
        sync.ChangeScope("b");
        release.TrySetResult();
        await old;

        Assert.Equal(QueryEventOutcome.Applied, await sync.ProcessAsync("Changed", new Envelope("b", 1, 1, "new scope")));
    }

    [Fact]
    public async Task Local_echoes_are_skipped_only_by_mappings_that_opt_in()
    {
        var client = NewClient();
        client.SetQueryData(TodoStats, 0);
        client.SetQueryData(TodoById(1), new Todo(1, "mine"));
        var sync = NewSync(client);
        sync.Map<Envelope>("Changed", m => m
            .WithMetadata(e => new QueryEventMetadata { CorrelationId = e.Id })
            .SetData(e => TodoById(e.TodoId), e => new Todo(e.TodoId, e.Title))
            .Covers(Todos)
            .IgnoreLocalEchoes());
        sync.Map<Envelope>("Changed", m => m.Update<int>(TodoStats, (n, _) => n + 1));

        sync.MarkLocalOrigin("mutation-7");
        await sync.ProcessAsync("Changed", new Envelope("mutation-7", 1, 1, "echo"));
        Assert.Equal("mine", client.GetQueryData<Todo>(TodoById(1)).Data!.Title); // echo skipped
        Assert.Equal(1, client.GetQueryData<int>(TodoStats).Data);                // other mapping still applies

        await sync.ProcessAsync("Changed", new Envelope("someone-else", 1, 1, "theirs"));
        Assert.Equal("theirs", client.GetQueryData<Todo>(TodoById(1)).Data!.Title);
    }

    // ------------------------------------------------------------------ races with fetches and mutations

    [Fact]
    public async Task A_fetch_that_started_before_the_event_cannot_overwrite_it()
    {
        var client = NewClient();
        var sync = NewSync(client);
        client.SetQueryData(TodoById(1), new Todo(1, "a", 1));
        var started = TestUtil.NewSignal();
        var release = TestUtil.NewSignal<Todo>();
        var fetches = 0;
        using var observer = client.Watch<Todo>(TodoById(1));
        var stale = client.FetchAsync(TodoById(1), async ct =>
        {
            if (Interlocked.Increment(ref fetches) == 1)
            {
                started.TrySetResult();
                return await release.Task.WaitAsync(ct); // returns the pre-event snapshot
            }

            return new Todo(1, "server after event", 2);
        }, forceRefetch: true);
        await started.Task;
        sync.Map<Todo>("Changed", m => m.Upsert(TodoEntity, t => t));

        await sync.ProcessAsync("Changed", new Todo(1, "from event", 2));
        release.TrySetResult(new Todo(1, "pre-event read", 1));
        await stale;
        await TestUtil.WaitUntilAsync(() => !client.GetQueryData<Todo>(TodoById(1)).IsFetching);

        Assert.Equal(2, Volatile.Read(ref fetches)); // the outdated fetch was superseded by a fresh one
        Assert.NotEqual("pre-event read", client.GetQueryData<Todo>(TodoById(1)).Data!.Title);
    }

    [Fact]
    public async Task A_first_fetch_in_flight_is_superseded_when_the_entity_names_its_detail_key()
    {
        var client = NewClient();
        var sync = NewSync(client);
        var keyed = QueryEntity.Create<Todo, int>(Todos, t => t.Id, detailKey: TodoById);
        var started = TestUtil.NewSignal();
        var release = TestUtil.NewSignal<Todo>();
        var fetches = 0;
        using var observer = client.Watch<Todo>(TodoById(1));
        var first = client.FetchAsync(TodoById(1), async ct =>
        {
            if (Interlocked.Increment(ref fetches) == 1)
            {
                started.TrySetResult();
                return await release.Task.WaitAsync(ct);
            }

            return new Todo(1, "fresh");
        });
        await started.Task;
        sync.Map<Todo>("Changed", m => m.Upsert(keyed, t => t));

        await sync.ProcessAsync("Changed", new Todo(1, "event"));
        release.TrySetResult(new Todo(1, "stale"));
        var result = await first;

        Assert.Equal("fresh", result.Data!.Title);
        Assert.Equal(2, Volatile.Read(ref fetches));
    }

    [Fact]
    public async Task An_event_during_a_pending_optimistic_update_survives_the_rollback()
    {
        var client = NewClient();
        var sync = NewSync(client);
        var server = new List<Todo> { new(1, "a"), new(2, "b") };
        using var observer = client.Watch<List<Todo>>(TodoList);
        await client.FetchAsync(TodoList, _ => Task.FromResult(server.ToList()));
        sync.Map<Todo>("Changed", m => m.Upsert(TodoEntity, t => t));
        var serverCall = TestUtil.NewSignal<Todo>();
        var mutation = client.CreateMutation<Todo, Todo>(
            (_, ct) => serverCall.Task.WaitAsync(ct),
            new MutationOptions<Todo, Todo>
            {
                OptimisticUpdates =
                [
                    OptimisticUpdate.Create<Todo, List<Todo>>(TodoList, (list, todo) => [.. list!.Select(t => t.Id == todo.Id ? todo : t)]),
                ],
            });

        var run = mutation.MutateAsync(new Todo(1, "optimistic", 2));
        await TestUtil.WaitUntilAsync(() => client.GetQueryData<List<Todo>>(TodoList).Data![0].Title == "optimistic");

        // Another user renames todo 2 while our mutation is pending.
        server[1] = new Todo(2, "B from someone else", 2);
        await sync.ProcessAsync("Changed", new Todo(2, "B from someone else", 2));
        Assert.Equal(["optimistic", "B from someone else"], client.GetQueryData<List<Todo>>(TodoList).Data!.Select(t => t.Title));
        serverCall.TrySetException(new HttpRequestException("rejected"));
        await Assert.ThrowsAnyAsync<Exception>(() => run);

        // The rollback must not restore the pre-mutation snapshot over the other user's change: the list is refetched.
        await TestUtil.WaitUntilAsync(() =>
            client.GetQueryData<List<Todo>>(TodoList) is { IsFetching: false, Data: [{ Title: "a" }, ..] });
        Assert.Equal(["a", "B from someone else"], client.GetQueryData<List<Todo>>(TodoList).Data!.Select(t => t.Title));
    }

    [Fact]
    public async Task A_mutation_response_older_than_a_received_event_does_not_win_with_entity_versions()
    {
        var client = NewClient();
        var sync = NewSync(client);
        client.SetQueryData(TodoList, new List<Todo> { new(1, "a", 1) });
        sync.Map<Todo>("Changed", m => m.Upsert(TodoEntity, t => t));

        // The server broadcasts version 3 (another user's later change) before our mutation's response (version 2).
        await sync.ProcessAsync("Changed", new Todo(1, "theirs", 3));
        // Applying the mutation response through the same entity rules keeps the newer state.
        await sync.ProcessAsync("Changed", new Todo(1, "our response", 2));

        Assert.Equal("theirs", client.GetQueryData<List<Todo>>(TodoList).Data![0].Title);
    }

    // ------------------------------------------------------------------ coalescing, overflow, lifecycle

    [Fact]
    public async Task A_burst_of_invalidations_refetches_each_query_once()
    {
        var client = NewClient();
        var fetches = 0;
        using var observer = client.Watch<List<Todo>>(TodoList);
        var gate = TestUtil.NewSignal();
        await client.FetchAsync(TodoList, Counting(() => new List<Todo>(), () => Interlocked.Increment(ref fetches)));
        var sync = NewSync(client);
        sync.Map<int>("Block", m => m.Handle(async (_, ct) => await gate.Task.WaitAsync(ct)).NoRecovery());
        sync.Map<TodoDeleted>("Deleted", m => m.Invalidate(Todos));

        Assert.True(sync.Post("Block", 0)); // keeps the loop busy while the burst queues up
        for (var i = 0; i < 100; i++)
        {
            Assert.True(sync.Post("Deleted", new TodoDeleted(i)));
        }

        gate.TrySetResult();
        await sync.ProcessAsync("Deleted", new TodoDeleted(-1));
        await TestUtil.WaitUntilAsync(() => !client.GetQueryData<List<Todo>>(TodoList).IsFetching);

        Assert.InRange(Volatile.Read(ref fetches), 2, 3); // initial fetch + one (or two at a burst boundary), not 101
    }

    [Fact]
    public async Task A_full_queue_drops_events_and_schedules_a_recovery()
    {
        var client = NewClient();
        await client.FetchAsync(TodoList, _ => Task.FromResult(new List<Todo>()));
        var gate = TestUtil.NewSignal();
        var sync = NewSync(client, options: new QueryEventSyncOptions { QueueCapacity = 4 });
        sync.Map<int>("Block", m => m.Handle(async (_, ct) => await gate.Task.WaitAsync(ct)).NoRecovery());
        sync.Map<Todo>("Changed", m => m.Upsert(TodoEntity, t => t));

        sync.Post("Block", 0);
        await TestUtil.WaitUntilAsync(() => sync.GetStatistics().QueueDepth == 0);
        var accepted = Enumerable.Range(0, 20).Count(i => sync.Post("Changed", new Todo(i, "x")));
        Assert.True(accepted < 20);
        Assert.Equal(20 - accepted, sync.GetStatistics().Dropped);

        gate.TrySetResult();
        await sync.ResynchronizeAsync();
        Assert.True(client.Cache.Describe(TodoList)!.IsStale);
        Assert.Equal(QuerySyncStatus.Synchronized, sync.Status);
    }

    [Fact]
    public async Task Disposal_cancels_queued_events_and_is_safe_under_concurrent_delivery()
    {
        var client = NewClient();
        var gate = TestUtil.NewSignal();
        var sync = new QueryEventSynchronizer(client);
        sync.Map<int>("Block", m => m.Handle(async (_, ct) => await gate.Task.WaitAsync(ct)).NoRecovery());
        sync.Map<TodoDeleted>("Deleted", m => m.Invalidate(Todos));

        var blocked = sync.ProcessAsync("Block", 0).AsTask();
        var queued = sync.ProcessAsync("Deleted", new TodoDeleted(1)).AsTask();
        var producers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                sync.Post("Deleted", new TodoDeleted(i));
            }
        })).ToArray();

        await sync.DisposeAsync();
        await Task.WhenAll(producers);

        Assert.Equal(QueryEventOutcome.Cancelled, await queued);
        Assert.Equal(QueryEventOutcome.Cancelled, await blocked);
        Assert.False(sync.Post("Deleted", new TodoDeleted(1)));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => sync.ProcessAsync("Deleted", new TodoDeleted(1)).AsTask());
        Assert.Equal(QuerySyncStatus.Disconnected, sync.Status);
        await sync.DisposeAsync(); // idempotent
    }

    [Fact]
    public async Task A_handler_may_dispose_its_own_synchronizer_without_deadlocking()
    {
        var client = NewClient();
        QueryEventSynchronizer? sync = null;
        sync = new QueryEventSynchronizer(client);
        sync.Map<int>("Stop", m => m.Handle(async (_, _) => await sync!.DisposeAsync()).NoRecovery());

        var outcome = await sync.ProcessAsync("Stop", 0).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(QueryEventOutcome.Applied, outcome);
        Assert.Equal(QuerySyncStatus.Disconnected, sync.Status);
    }

    [Fact]
    public async Task Removing_the_last_mapping_unsubscribes_from_the_source_and_mappings_never_subscribe_twice()
    {
        var client = NewClient();
        var source = new InMemoryQueryEventSource();
        var sync = NewSync(client, source);
        var first = sync.Map<Todo>("Changed", m => m.Upsert(TodoEntity, t => t));
        var second = sync.Map<Todo>("Changed", m => m.Invalidate(TodoStats));
        Assert.Equal(1, source.SubscriberCount("Changed"));

        first.Dispose();
        Assert.Equal(1, source.SubscriberCount("Changed"));
        second.Dispose();
        second.Dispose();
        Assert.Equal(0, source.SubscriberCount("Changed"));
        Assert.Empty(sync.GetStatistics().Mappings);
    }

    [Fact]
    public async Task Source_events_flow_into_the_cache_and_reconnects_recover_once()
    {
        var client = NewClient();
        var fetches = 0;
        using var observer = client.Watch<List<Todo>>(TodoList);
        await client.FetchAsync(TodoList, Counting(() => new List<Todo> { new(1, "a") }, () => Interlocked.Increment(ref fetches)));
        var source = new InMemoryQueryEventSource();
        var sync = NewSync(client, source, new QueryEventSyncOptions { RecoverOnConnect = false });
        var statuses = new List<QuerySyncStatus>();
        sync.StatusChanged += change => { lock (statuses) { statuses.Add(change.Status); } };
        sync.Map<Todo>("Changed", m => m.Upsert(TodoEntity, t => t));
        Assert.Equal(QuerySyncStatus.Disconnected, sync.Status);

        await sync.StartAsync();
        Assert.Equal(QuerySyncStatus.Synchronized, sync.Status);
        Assert.True(await source.PublishAsync("Changed", new Todo(1, "A", 2)));
        await TestUtil.WaitUntilAsync(() => client.GetQueryData<List<Todo>>(TodoList).Data![0].Title == "A");

        // Connection lost: an event published meanwhile is missed.
        source.SetState(QueryEventSourceState.Reconnecting, new IOException("network"));
        Assert.False(await source.PublishAsync("Changed", new Todo(1, "missed", 3)));
        Assert.Equal(QuerySyncStatus.Reconnecting, sync.Status);

        // Reconnected (twice in a row, as some transports report): one recovery refetches the covered queries.
        source.SetState(QueryEventSourceState.Resubscribing);
        source.SetState(QueryEventSourceState.Connected);
        source.SetState(QueryEventSourceState.Connected);
        await TestUtil.WaitUntilAsync(() => sync.Status == QuerySyncStatus.Synchronized && Volatile.Read(ref fetches) >= 2);
        await Task.Delay(50);

        Assert.InRange(Volatile.Read(ref fetches), 2, 3);
        lock (statuses)
        {
            Assert.Contains(QuerySyncStatus.Reconnecting, statuses);
            Assert.Contains(QuerySyncStatus.Resubscribing, statuses);
            Assert.Contains(QuerySyncStatus.Synchronizing, statuses);
        }

        Assert.True(sync.GetStatistics().Recoveries >= 1);
    }

    [Fact]
    public async Task A_permanently_failed_source_reports_disconnected_with_its_error()
    {
        var client = NewClient();
        var source = new InMemoryQueryEventSource();
        var sync = NewSync(client, source);
        sync.Map<Todo>("Changed", m => m.Upsert(TodoEntity, t => t));
        await sync.StartAsync();
        Exception? reported = null;
        sync.StatusChanged += change => reported = change.Error;

        source.SetState(QueryEventSourceState.Disconnected, new UnauthorizedAccessException("401"));

        Assert.Equal(QuerySyncStatus.Disconnected, sync.Status);
        Assert.IsType<UnauthorizedAccessException>(reported);
        Assert.Contains(sync.GetStatistics().RecentErrors, e => e.ErrorType == nameof(UnauthorizedAccessException));
    }

    [Fact]
    public async Task Recovery_on_first_connect_reconciles_queries_fetched_before_the_subscription()
    {
        var client = NewClient();
        await client.FetchAsync(TodoList, _ => Task.FromResult(new List<Todo>()));
        var source = new InMemoryQueryEventSource();
        var sync = NewSync(client, source);
        sync.Map<Todo>("Changed", m => m.Upsert(TodoEntity, t => t));

        await sync.StartAsync();
        await TestUtil.WaitUntilAsync(() => sync.Status == QuerySyncStatus.Synchronized);

        Assert.True(client.Cache.Describe(TodoList)!.IsStale);
        Assert.Equal([Todos], sync.GetStatistics().CoveredKeys);
    }

    [Fact]
    public async Task Envelopes_provide_their_own_metadata()
    {
        var client = NewClient();
        client.SetQueryData(TodoStats, 0);
        var sync = NewSync(client);
        sync.Map<QueryEventEnvelope<int>>("Counted", m => m.Update<int>(TodoStats, (n, e) => n + e.Payload));

        await sync.ProcessAsync("Counted", new QueryEventEnvelope<int> { EventId = "1", Sequence = 1, Payload = 5 });
        Assert.Equal(QueryEventOutcome.Duplicate, await sync.ProcessAsync("Counted", new QueryEventEnvelope<int> { EventId = "1", Sequence = 1, Payload = 5 }));
        Assert.Equal(QueryEventOutcome.Stale, await sync.ProcessAsync("Counted", new QueryEventEnvelope<int> { EventId = "0", Sequence = 1, Payload = 5 }));

        Assert.Equal(5, client.GetQueryData<int>(TodoStats).Data);
    }

    [Fact]
    public async Task Processing_is_reported_on_the_diagnostic_stream()
    {
        var client = NewClient();
        var events = new List<DotNetQueryEvent>();
        client.DiagnosticEvent += e => { lock (events) { events.Add(e); } };
        var sync = NewSync(client, options: new QueryEventSyncOptions { Name = "todos-hub" });
        sync.Map<Todo>("Changed", m => m.Upsert(TodoEntity, t => t));

        await sync.ProcessAsync("Changed", new Todo(1, "a"));
        await sync.ResynchronizeAsync();

        lock (events)
        {
            Assert.Contains(events, e => e is QuerySyncEventProcessedEvent { Synchronizer: "todos-hub", EventName: "Changed", Outcome: QueryEventOutcome.Applied });
            Assert.Contains(events, e => e is QuerySyncRecoveryCompletedEvent { Reason: QuerySyncRecoveryReason.Manual, Succeeded: true });
            Assert.Contains(events, e => e is QuerySyncStatusChangedEvent { Status: QuerySyncStatus.Synchronizing });
        }
    }
}
