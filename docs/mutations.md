# Mutations

Queries read server state; **mutations** change it (create, update, delete, or any custom operation). A
mutation runs a function, tracks its lifecycle, applies optimistic updates, refreshes dependent queries, and
fires callbacks.

## A one-shot mutation

```csharp
var result = await client.MutateAsync<CreateTodo, Todo>(
    (input, ct) => api.CreateTodoAsync(input, ct),
    new CreateTodo("Buy milk"),
    new MutationOptions<CreateTodo, Todo> { Invalidates = [TodoQueries.All] });

Console.WriteLine($"Created #{result.Data!.Id}");
```

`MutateAsync<TVariables, TData>` returns a `MutationResult<TVariables, TData>` (`Data`, `Error`, `Status`,
`IsSuccess`, `IsError`, `EnsureData()`). By default a failure throws after retries (`ThrowOnError = true`);
set `ThrowOnError = false` to capture it into `result.Error` instead.

## Reusable, observable mutations

For UI, create a mutation once and observe it:

```csharp
var addTodo = client.CreateMutation<CreateTodo, Todo>(
    (input, ct) => api.CreateTodoAsync(input, ct),
    new MutationOptions<CreateTodo, Todo>
    {
        MutationKey = QueryKey.Create("todos", "add"),   // shown in DevTools and diagnostics
        Invalidates = [TodoQueries.All],
    });

addTodo.StateChanged += state => Render(state);           // Idle → Pending → Success/Error

await addTodo.MutateAsync(new CreateTodo("Buy milk"));
```

`MutationState` exposes `IsIdle`, `IsPending`, `IsSuccess`, `IsError`, `Data`, `Error`, `Variables`,
`SubmittedAt`, and `FailureCount`. Call `Reset()` to return to idle. A mutation's observable state always
tracks its **most recent** run: if you call `MutateAsync` again before an earlier run settles, the earlier
run still completes (its callbacks fire and its task returns its own result) but no longer drives the state.

## Refreshing queries after a mutation

`Invalidates` lists the query keys (prefix-matched) to invalidate once the mutation settles - after success
**or** failure, so a failed optimistic update is also reconciled with the server. The mutation stays
`Pending` until the resulting refetches of active queries complete, so a "Saving…" indicator covers the
whole round trip - except for queries that still carry optimistic updates of other pending mutations: their refetch
waits until those settle (the last one runs it), so a burst of mutations is reconciled once and never shows the server's
value without the still-pending changes (see [Optimistic updates](optimistic-updates.md#concurrent-mutations-of-one-list)).
A refetch that started after this mutation's server call settled already includes its write, so the invalidation joins
it instead of cancelling and restarting it - mutations settling together refetch a query once.

## Lifecycle

```
OptimisticUpdates → OnMutate → mutation function (with retries)
    → OnSuccess | (roll back optimistic updates) OnError
    → OnSettled → Invalidates
```

```csharp
new MutationOptions<CreateTodo, Todo>
{
    OnMutate  = (vars, ct) => ValueTask.FromResult<object?>(null),
    OnSuccess = (data, vars, ctx, ct) => { logger.LogInformation("created {Id}", data.Id); return ValueTask.CompletedTask; },
    OnError   = (err, vars, ctx, ct) => ValueTask.CompletedTask,
    OnSettled = (data, err, vars, ctx, ct) => ValueTask.CompletedTask,
}
```

Guarantees:

- The mutation function is **never re-run because a callback threw**. Retries only apply to failures of the
  mutation function itself.
- A throwing `OnMutate`, `OnSuccess` or `OnSettled` turns the run into a failure (so you hear about it).
  Optimistic updates are rolled back only if the server did **not** accept the write.
- **Cancellation** (the `CancellationToken` passed to `MutateAsync`) before the write is accepted rolls back
  optimistic updates, runs `OnError`/`OnSettled` with `CancellationToken.None`, and then throws
  `OperationCanceledException`. Cancelling while waiting for the `Invalidates` refetches only stops the wait.
- Settle-phase callback exceptions never mask the original error or skip the remaining cleanup.

The value returned from `OnMutate` is threaded through the later callbacks as `ctx`. For optimistic
updates, prefer the declarative `OptimisticUpdates` - see [Optimistic Updates](optimistic-updates.md).

## Retries

```csharp
new MutationOptions<CreateTodo, Todo>
{
    RetryCount = 3,
    RetryDelay = RetryPolicy.ExponentialWithJitter(TimeSpan.FromSeconds(1)),
    ShouldRetry = HttpRetryPolicy.ShouldRetry, // never retry a 400/401/403/404/409
}
```

Only retry idempotent operations. `ShouldRetry` can only veto retries within the `RetryCount` budget. `RetryCount`
left unset (`null`) uses the matching `MutationDefaults`, else 0; set `RetryCount = 0` to opt a mutation out of retries
its defaults enable.

## Defaults for families of mutations

```csharp
builder.Services.AddDotNetQuery(o => o
    .SetMutationDefaults(new MutationDefaults { ShouldRetry = HttpRetryPolicy.ShouldRetry })            // every mutation
    .SetMutationDefaults(QueryKey.Create("todos"), new MutationDefaults                                    // MutationKey ["todos", ...]
    {
        RetryCount = 2,
        Scope = "todos",
        Invalidates = [TodoQueries.All],
    }));
```

A mutation's own options win; unset (`null`) ones come from the most specific matching defaults, including `Meta`
(application data for global handlers - see [Diagnostics](diagnostics.md#query-and-mutation-meta)).

## Progress and deduplication

```csharp
// Report progress (0..1) from the mutation function: State.Progress updates and observers re-render.
var upload = client.CreateMutation<Stream, Uri>((file, progress, ct) => api.UploadAsync(file, progress, ct));

// Join a pending run with equal variables instead of starting another (idempotent operations only).
var markRead = client.CreateMutation<int, Unit>(api.MarkReadAsync, new() { DeduplicatePending = true });
```

## Serial execution with scopes

Runs that share a `Scope` execute one at a time, in the order they were started - across `Mutation` instances -
so dependent writes reach the server in order (create a record, then rename it):

```csharp
var options = new MutationOptions<TodoEdit, Todo> { Scope = $"todo-{id}" };
var rename = client.CreateMutation<TodoEdit, Todo>(api.RenameAsync, options);
var toggle = client.CreateMutation<TodoEdit, Todo>(api.ToggleAsync, options);
```

A run waiting for its turn is `Pending` with `IsPaused = true`; its optimistic updates and `OnMutate` apply
immediately (the UI updates at once), only the mutation function waits. The scope is released once a run fully
settles - callbacks and invalidation included - whether it succeeded, failed or was cancelled. A run cancelled
while waiting rolls back its optimistic updates and never lets later runs overtake the ones still ahead of it.

## The mutation cache

`client.Cache.Mutations` tracks every run - pending ones and, for `QueryClientOptions.MutationCacheTime`
(default 5 minutes), settled ones - so any part of the app can observe writes without holding the `Mutation`
instances:

```csharp
var saving = client.Cache.Mutations.CountPending();                                   // global "saving…" badge
var failed = client.Cache.Mutations.FindAll(new MutationFilter { Status = MutationStatus.Error });
var todoWrites = client.Cache.Mutations.FindAll(new MutationFilter { MutationKey = QueryKey.Create("todos") });

// Global lifecycle hook: react to every mutation, wherever it was started.
client.Cache.Mutations.Changed += change =>
{
    if (client.Cache.Mutations.Find(change.MutationId) is { Status: MutationStatus.Error } failedRun)
    {
        toasts.Show($"Saving failed: {failedRun.Error!.Message}");
    }
};
```

Each `MutationDescriptor` carries the id (matching the diagnostic events), key, scope, status, `IsPaused`,
variables, data, error, failure count and timestamps. Settled runs are dropped by the client's garbage
collector after `MutationCacheTime` (`TimeSpan.Zero` drops them on settle); `Clear()` drops settled runs now.

## Diagnostics

Every run gets a client-unique `MutationId`; `MutationStartedEvent` carries it with the `MutationKey` and
variables, and `MutationCompletedEvent` reports the outcome, failure count and duration. The DevTools
mutation history is built from these events.

## Offline

Wrap mutation invocations in an `OfflineMutationQueue` (in memory) or a `PersistentMutationQueue` (durable
across restarts) to defer and replay them when connectivity returns - see [Offline Mode](offline-mode.md).
