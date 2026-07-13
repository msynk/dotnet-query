using DotNetQuery;
using DotNetQuery.Sample.ECommerce;

// Demonstrates optimistic updates with rollback: the cart updates instantly when you add an item, then
// reconciles with the server - and if the server rejects the add, the optimistic change is rolled back.
// Deterministic and self-terminating so it doubles as an end-to-end smoke test (exit 0 == pass).

using var client = new QueryClient(new QueryClientOptions
{
    DefaultStaleTime = TimeSpan.FromMinutes(5),
    DefaultRetryCount = 0,
    DefaultRetryDelay = RetryPolicy.Constant(TimeSpan.Zero),
});
var api = new StoreApi();
var cartKey = QueryKey.Create("cart");

static string Render(IReadOnlyList<string>? cart) => cart is { Count: > 0 } ? string.Join(", ", cart) : "(empty)";
string CurrentCart() => Render(client.GetQueryData<List<string>>(cartKey).Data);

// Keep the cart observed so post-mutation invalidation refetches it from the server.
using var observer = client.Watch<List<string>>(cartKey);

Console.WriteLine("=== ECommerce: cart with optimistic updates ===\n");
await client.FetchAsync(cartKey, api.GetCartAsync);
Console.WriteLine($"Cart (initial): {CurrentCart()}\n");

async Task<bool> AddToCart(string item)
{
    Console.WriteLine($"--- Add \"{item}\" ---");
    var result = await client.MutateAsync<string, List<string>>(
        (i, ct) => api.AddToCartAsync(i, ct),
        item,
        new MutationOptions<string, List<string>>
        {
            ThrowOnError = false,
            // Optimistically add the item to the cached cart *before* the server responds, and return the
            // pre-change cart as the rollback context.
            OnMutate = (i, ct) =>
            {
                var previous = client.GetQueryData<List<string>>(cartKey).Data ?? [];
                client.SetQueryData(cartKey, new List<string>(previous) { i });
                Console.WriteLine($"  [optimistic] cart: {CurrentCart()}   (server not called yet)");
                return new ValueTask<object?>(previous);
            },
            // On failure, restore the pre-change cart.
            OnError = (ex, i, context, ct) =>
            {
                client.SetQueryData(cartKey, (List<string>)context!);
                Console.WriteLine($"  [error] {ex.Message} → rolled back to: {CurrentCart()}");
                return ValueTask.CompletedTask;
            },
            // Always reconcile with the server afterward.
            OnSettled = (data, err, i, context, ct) => new ValueTask(client.InvalidateQueriesAsync(cartKey, ct)),
        });

    if (result.IsSuccess)
    {
        Console.WriteLine($"  [confirmed] server cart: {CurrentCart()}");
    }

    Console.WriteLine();
    return result.IsSuccess;
}

var added1 = await AddToCart("Keyboard"); // succeeds
var added2 = await AddToCart("Mouse");    // out of stock → rolled back

var finalCart = client.GetQueryData<List<string>>(cartKey).Data ?? [];
Console.WriteLine($"Final cart: {Render(finalCart)}   (server: {Render(api.ServerCart)})");

var ok = added1 && !added2
    && finalCart is ["Keyboard"]
    && api.ServerCart.Count == 1;
Console.WriteLine(ok ? "\nDONE (pass)" : "\nDONE (FAIL)");
return ok ? 0 : 1;
