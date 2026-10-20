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
whole round trip.

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
}
```

Only retry idempotent operations.

## Diagnostics

Every run gets a client-unique `MutationId`; `MutationStartedEvent` carries it with the `MutationKey` and
variables, and `MutationCompletedEvent` reports the outcome, failure count and duration. The DevTools
mutation history is built from these events.

## Offline

Wrap mutation invocations in an `OfflineMutationQueue` (in memory) or a `PersistentMutationQueue` (durable
across restarts) to defer and replay them when connectivity returns - see [Offline Mode](offline-mode.md).
