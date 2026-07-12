namespace DotNetQuery.Sample.Maui;

/// <summary>
/// A stand-in data source with simulated latency, so the sample exercises the query lifecycle
/// (loading → success, refetch) without a real backend. The fetch counter makes refetches visible.
/// </summary>
public sealed class TodoService
{
    private static readonly string[] Seed =
    [
        "Buy milk",
        "Walk the dog",
        "Ship DotNetQuery 1.0",
    ];

    private int _fetches;

    public async Task<List<string>> GetTodosAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(700, cancellationToken); // simulate a network round-trip
        var n = Interlocked.Increment(ref _fetches);
        return Seed.Select(s => $"{s}   (fetch #{n})").ToList();
    }
}
