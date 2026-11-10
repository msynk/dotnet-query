using DotNetQuery;
using DotNetQuery.Demo.ECommerce;

// Demonstrates declarative optimistic updates: the cart and its item count update instantly (atomically, as one
// transaction) when you add an item, then reconcile with the server. Three adds run at the same time; the server
// rejects one (out of stock) and only that item is rolled back - the others' optimistic changes stay. Each add
// invalidates the cart, but a refetch never lands while other adds are still pending: it waits until the last one
// settled, so the burst is reconciled with one refetch and the cart never flashes the server's value without the
// pending items. Permanent failures (out of stock, 409) are not retried. Deterministic and self-terminating, so it
// doubles as an end-to-end smoke test (exit 0 == pass).

using var client = new QueryClient(new QueryClientOptions
{
    DefaultStaleTime = TimeSpan.FromMinutes(5),
    DefaultRetryCount = 0,
});
var api = new StoreApi();
var cartKey = QueryKey.Create("cart");
var countKey = QueryKey.Create("cart", "count");

static string Render(IReadOnlyList<string>? cart) => cart is { Count: > 0 } ? string.Join(", ", cart) : "(empty)";

// Keep both queries observed, so the invalidation after each add refetches them from the server.
using var cartObserver = client.Watch<List<string>>(cartKey);
using var countObserver = client.Watch<int>(countKey);
cartObserver.StateChanged += state => Console.WriteLine($"  cart -> {Render(state.Data)}{(state.IsFetching ? "  (syncing)" : "")}");

Console.WriteLine("=== ECommerce: cart with declarative optimistic updates ===\n");
await client.FetchAsync(cartKey, api.GetCartAsync);
await client.FetchAsync(countKey, api.GetCountAsync);

var addKey = QueryKey.Create("cart", "add");
var addToCart = client.CreateMutation<string, List<string>>(
    api.AddToCartAsync,
    new MutationOptions<string, List<string>>
    {
        MutationKey = addKey,
        ThrowOnError = false,
        // Retry transient failures only: a 409 conflict or "out of stock" fails at once.
        RetryCount = 2,
        ShouldRetry = (_, error) => error is not InvalidOperationException && !HttpRetryPolicy.IsConflict(error),
        // Both keys are written before the request starts, in one transaction, and rolled back exactly on failure.
        OptimisticUpdates =
        [
            OptimisticUpdate.Create<string, List<string>>(cartKey, (cart, item) => [.. cart ?? [], item]),
            OptimisticUpdate.Create<string, int>(countKey, (count, _) => count + 1),
        ],
        // Reconcile with the server once settled; while other adds are pending, the refetch waits for them.
        Invalidates = [cartKey],
    });

Console.WriteLine("--- Add Keyboard, Mouse and Monitor at once ---");
string[] items = ["Keyboard", "Mouse", "Monitor"];
var results = await Task.WhenAll(items.Select(item => addToCart.MutateAsync(item)));
foreach (var (item, result) in items.Zip(results).Where(p => p.Second.IsError))
{
    Console.WriteLine($"  [rejected] {item}: {result.Error!.Message}");
}

// The last add to settle ran the one refetch of the burst: wait for it to land.
while (client.Cache.Describe(cartKey) is { Status: QueryStatus.Refetching } || api.CartFetches < 2)
{
    await Task.Delay(10);
}

var finalCart = client.GetQueryData<List<string>>(cartKey).Data ?? [];
var finalCount = client.GetQueryData<int>(countKey).Data;
Console.WriteLine($"\nFinal cart: {Render(finalCart)}, count {finalCount}   (server: {Render(api.ServerCart)})");

var ok = results.Count(r => r.IsSuccess) == 2
    && finalCart.Order().SequenceEqual(["Keyboard", "Monitor"])
    && finalCount == 2
    && api.ServerCart.Count == 2
    && api.Attempts["Mouse"] == 1    // the rejected add was not retried
    && api.CartFetches == 2;         // the initial load and one reconciliation for the whole burst
Console.WriteLine(ok ? "\nDONE (pass)" : "\nDONE (FAIL)");
return ok ? 0 : 1;
