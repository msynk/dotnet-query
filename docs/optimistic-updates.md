# Optimistic Updates

An optimistic update applies the expected result to the cache *before* the server confirms it, so the UI
feels instant - then rolls back if the request fails, and reconciles with the server either way.

## Declarative (recommended)

```csharp
var addTodo = client.CreateMutation<string, Todo>(
    (title, ct) => api.AddAsync(title, ct),
    new MutationOptions<string, Todo>
    {
        OptimisticUpdates =
        [
            OptimisticUpdate.Create<string, List<Todo>>(
                TodoQueries.All,                                       // the exact query key to write
                (todos, title) => [.. todos ?? [], new Todo(-1, title, Done: false)]),
        ],
        Invalidates = [TodoQueries.All],                               // reconcile once settled
    });
```

For each `OptimisticUpdate`, before the mutation function runs, DotNetQuery:

1. cancels any in-flight fetch for the key, so a refetch that started earlier cannot overwrite the
   optimistic value when it lands;
2. snapshots the entry's full state (data, timestamp, staleness);
3. writes `update(current, variables)` - all updates of the mutation in **one atomic transaction**, so
   observers never see one list updated and another not, and an update that throws writes nothing.

If the mutation fails or is cancelled before the server accepts it, every snapshot is restored atomically -
exactly, including timestamps and pending invalidations (several updates of the same key restore to the
original value).

**Overlapping mutations.** Optimistic writes on a query stack up as layers. When a mutation whose layer has
other pending (or already accepted) mutations' layers above it fails, those layers are re-applied on its
snapshot - the failed item disappears, the others stay, and nothing is refetched. Settling in any order works:
the query always shows exactly the accepted and still-pending changes. Only when something *other* than an
optimistic update wrote to the query in between (a refetch, `SetQueryData`), or a re-applied update throws, is
the query invalidated and refetched instead, so that write is never clobbered.

With a `QueryDefinition`, use the extension: `TodoQueries.List(api).Optimistic<List<Todo>, string>(...)`.

### Concurrent mutations of one list

When several mutations of the same list are in flight (a user adding items quickly), refetching the list while some of
them are still pending would replace their optimistic items with server data that does not include them yet. So while
optimistic writes of pending mutations are on a query, its refreshes wait: invalidation (`Invalidates`,
`InvalidateQueriesAsync`), `RefetchQueryAsync`, refetch intervals, focus/reconnect triggers and reads of stale data mark
it stale and return the current (optimistic) state, an outdated fetch in flight is cancelled, and **one** refetch runs
once the last of those mutations has settled (unless one already started). Every mutation can keep `Invalidates`; the
burst is reconciled with a single refetch and the list never flashes the server's value without the pending items.

- A mutation whose invalidation waits for others completes without waiting for that refetch (the last one to settle
  runs it), so serial `Scope`s never wait on each other.
- An inactive query is only marked stale; it refetches on its next use.
- Loading an infinite query's next or previous page is not a refresh and runs at once.

The [ECommerce demo](https://github.com/msynk/dotnet-query/tree/main/demo/DotNetQuery.Demo.ECommerce) shows it:
three concurrent adds, one rejected and rolled back alone, one reconciliation at the end.

Updating several lists (for example "all" and "open" todos) is just more entries in `OptimisticUpdates`.

## Manual (full control)

When the optimistic change can't be expressed per key, use the callbacks:

```csharp
await client.MutateAsync<Todo, Todo>(
    (todo, ct) => api.UpdateTodoAsync(todo, ct),
    editedTodo,
    new MutationOptions<Todo, Todo>
    {
        OnMutate = async (todo, ct) =>
        {
            var key = QueryKey.Create<Todo>(todo.Id);
            await client.CancelQueriesAsync(key, ct);              // don't let a refetch overwrite us
            var previous = client.GetQueryData<Todo>(key).Data;    // snapshot
            client.SetQueryData(key, todo);                        // optimistic
            return previous;                                       // context for rollback
        },
        OnError = (error, todo, context, ct) =>
        {
            client.SetQueryData(QueryKey.Create<Todo>(todo.Id), (Todo)context!);  // rollback
            return ValueTask.CompletedTask;
        },
        Invalidates = [QueryKey.Create<Todo>()],                    // reconcile with the server
    });
```

## Infinite lists

Infinite queries store their pages in the cache as `InfiniteData<TParam, TPage>`, so they can be updated
optimistically too:

```csharp
OptimisticUpdate.Create<Message, InfiniteData<string, MessagePage>>(
    ChatQueries.Room(roomId),
    (data, message) => data! with { Pages = [data.Pages[0] with { Items = [message, .. data.Pages[0].Items] }, .. data.Pages.Skip(1)] })
```

## Tips

- Keep optimistic state minimal - write only what changes, and return new instances (never mutate the
  cached value).
- Always pair optimistic updates with `Invalidates` so the client converges on the server's truth even if
  the optimistic guess was slightly off.
- Mark placeholder rows (for example a negative id) so the UI can show "saving…" and disable actions on
  them until the real data arrives.
