namespace DotNetQuery.Sample.ECommerce;

/// <summary>
/// A stand-in store server holding the authoritative cart. Adds have simulated latency so the optimistic
/// update is visible before the server responds; "Mouse" is out of stock and is rejected, to exercise
/// rollback.
/// </summary>
public sealed class StoreApi
{
    private readonly List<string> _cart = [];

    public IReadOnlyList<string> ServerCart => _cart;

    public Task<List<string>> GetCartAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new List<string>(_cart));

    public async Task<List<string>> AddToCartAsync(string item, CancellationToken cancellationToken)
    {
        await Task.Delay(150, cancellationToken); // simulate a network round-trip
        if (item == "Mouse")
        {
            throw new InvalidOperationException($"'{item}' is out of stock");
        }

        _cart.Add(item);
        return new List<string>(_cart);
    }
}
