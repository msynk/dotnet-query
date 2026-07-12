namespace DotNetQuery.Sample.Avalonia;

/// <summary>
/// A stand-in data source with a little simulated latency so the sample shows the query lifecycle
/// (loading → success, refetch). The fetch counter makes refetches visible.
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
        await Task.Delay(150, cancellationToken); // simulate a round-trip
        var n = Interlocked.Increment(ref _fetches);
        return Seed.Select(s => $"{s}   (fetch #{n})").ToList();
    }
}
