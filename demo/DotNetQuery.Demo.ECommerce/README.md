# DotNetQuery.Sample.ECommerce

A console walkthrough of **declarative optimistic updates** - the mutation feature cluster - with no UI-framework
noise. [Program.cs](Program.cs) adds three items to a cart at the same time through one mutation whose
`OptimisticUpdates` write the cart and its item count:

1. **Optimistically** - both keys update in one atomic transaction before the server responds (instant feedback).
2. **Overlapping rollback** - the server rejects "Mouse" (out of stock); only that item is rolled back, while the
   optimistic changes of the two adds still in flight stay (optimistic layers), with no refetch in between.
3. **Retry classification** - permanent failures (out of stock, `409` via `HttpRetryPolicy.IsConflict`) are not
   retried; transient ones would be (`RetryCount`, `ShouldRetry`).
4. **Reconcile** - every add invalidates the cart, but while other adds are still pending the refetch waits for them:
   the last add to settle runs one refetch for the whole burst, and the cart never flashes the server's value without
   the pending items (see [Optimistic updates](../../docs/optimistic-updates.md#concurrent-mutations-of-one-list)).

[StoreApi](StoreApi.cs) stands in for the server.

## Run it

```bash
dotnet run --project samples/DotNetQuery.Sample.ECommerce
```

Expected: the cart shows all three items optimistically, rolls "Mouse" back, and ends with the server's
`Keyboard, Monitor` and a count of 2, after exactly one refetch. Deterministic and exits `0` on success, so it also runs as an end-to-end smoke
test in CI.
