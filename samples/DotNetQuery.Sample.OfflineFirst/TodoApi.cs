namespace DotNetQuery.Sample.OfflineFirst;

/// <summary>
/// A stand-in server holding the authoritative to-do list. <see cref="Reads"/> counts read round-trips so
/// the sample can prove that offline reads are served from the cache without hitting the network.
/// </summary>
public sealed class TodoApi
{
    private readonly List<string> _server = ["Buy milk", "Walk the dog", "Ship DotNetQuery 1.0"];

    public int Reads { get; private set; }

    public int ServerCount => _server.Count;

    public Task<List<string>> GetTodosAsync(CancellationToken cancellationToken)
    {
        Reads++;
        return Task.FromResult(new List<string>(_server));
    }

    public Task AddTodoAsync(string text, CancellationToken cancellationToken)
    {
        _server.Add(text);
        return Task.CompletedTask;
    }
}
