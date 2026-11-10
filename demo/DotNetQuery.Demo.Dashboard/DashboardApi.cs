namespace DotNetQuery.Sample.Dashboard;

/// <summary>
/// A stand-in metrics backend. Each endpoint counts its calls so the sample can prove request
/// deduplication (two concurrent requests for one key → a single call) and background refresh.
/// </summary>
public sealed class DashboardApi
{
    private int _users;
    private int _revenue;
    private int _orders;
    private int _visitors;

    public int UsersCalls => _users;
    public int RevenueCalls => _revenue;
    public int OrdersCalls => _orders;

    public async Task<int> GetActiveUsersAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(120, cancellationToken); // latency, so concurrent requests overlap and deduplicate
        Interlocked.Increment(ref _users);
        return 1234;
    }

    public async Task<decimal> GetRevenueAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(90, cancellationToken);
        Interlocked.Increment(ref _revenue);
        return 56_789m;
    }

    public async Task<int> GetPendingOrdersAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(70, cancellationToken);
        Interlocked.Increment(ref _orders);
        return 42;
    }

    // Increments each call so a background refresh is visible.
    public Task<int> GetLiveVisitorsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(300 + Interlocked.Increment(ref _visitors));
}
