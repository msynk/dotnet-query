using System.Collections.Concurrent;

namespace DotNetQuery.Demo.ECommerce;

/// <summary>
/// A stand-in store server holding the authoritative cart. Adds have simulated latency so the optimistic
/// update is visible before the server responds; "Mouse" is out of stock and is rejected, to exercise
/// rollback.
/// </summary>
public sealed class StoreApi
{
    private readonly List<string> _cart = [];

    public IReadOnlyList<string> ServerCart
    {
        get { lock (_cart) { return _cart.ToList(); } }
    }

    /// <summary>How many times each item's add reached the server.</summary>
    public ConcurrentDictionary<string, int> Attempts { get; } = new();

    /// <summary>How many times the cart was read.</summary>
    public int CartFetches => Volatile.Read(ref _cartFetches);

    private int _cartFetches;

    public Task<List<string>> GetCartAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _cartFetches);
        lock (_cart)
        {
            return Task.FromResult(new List<string>(_cart));
        }
    }

    public Task<int> GetCountAsync(CancellationToken cancellationToken)
    {
        lock (_cart)
        {
            return Task.FromResult(_cart.Count);
        }
    }

    public async Task<List<string>> AddToCartAsync(string item, CancellationToken cancellationToken)
    {
        Attempts.AddOrUpdate(item, 1, (_, n) => n + 1);
        // Simulated round-trips of different lengths: the server answers Mouse, then Keyboard, then Monitor.
        await Task.Delay(item switch { "Mouse" => 80, "Keyboard" => 150, _ => 250 }, cancellationToken);
        if (item == "Mouse")
        {
            throw new InvalidOperationException($"'{item}' is out of stock");
        }

        lock (_cart)
        {
            _cart.Add(item);
            return new List<string>(_cart);
        }
    }
}
