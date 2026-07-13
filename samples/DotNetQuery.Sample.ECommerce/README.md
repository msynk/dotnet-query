# DotNetQuery.Sample.ECommerce

A console walkthrough of **optimistic updates with rollback** - the mutation feature cluster - with no
UI-framework noise. [Program.cs](Program.cs) adds items to a cart, and each add:

1. **Optimistically** updates the cached cart *before* the server responds (instant UI feedback), keeping
   the previous cart as a rollback context (`OnMutate`).
2. **Rolls back** to the previous cart if the server rejects the add (`OnError`).
3. **Reconciles** with the server afterward via invalidation (`OnSettled`).

The run adds "Keyboard" (succeeds) then "Mouse" (out of stock → rolled back). [StoreApi](StoreApi.cs)
stands in for the server.

## Run it

```bash
dotnet run --project samples/DotNetQuery.Sample.ECommerce
```

Expected: the cart shows each item optimistically, confirms "Keyboard", and rolls the failed "Mouse" add
back to `Keyboard`. Deterministic and exits `0` on success, so it also runs as an end-to-end smoke test in
CI.
