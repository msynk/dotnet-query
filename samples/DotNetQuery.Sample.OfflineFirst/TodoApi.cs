using System.Net;

namespace DotNetQuery.Sample.OfflineFirst;

/// <summary>
/// A stand-in server holding the authoritative to-do list. <see cref="Reads"/> counts read round-trips so
/// the sample can prove that offline reads are served from the cache without hitting the network, and writes carry an
/// idempotency key the server deduplicates on.
/// </summary>
public sealed class TodoApi
{
    private readonly List<string> _server = ["Buy milk", "Walk the dog", "Ship DotNetQuery 1.0"];
    private readonly HashSet<string> _appliedKeys = [];

    public int Reads { get; private set; }

    public int ServerCount => _server.Count;

    /// <summary>The number of distinct idempotency keys the server applied.</summary>
    public int AppliedKeys => _appliedKeys.Count;

    public Task<List<string>> GetTodosAsync(CancellationToken cancellationToken)
    {
        Reads++;
        return Task.FromResult(new List<string>(_server));
    }

    public Task AddTodoAsync(string text, string idempotencyKey, CancellationToken cancellationToken)
    {
        if (text.StartsWith("conflict", StringComparison.Ordinal))
        {
            throw new HttpRequestException("409 Conflict: the todo was deleted on another device", null, HttpStatusCode.Conflict);
        }

        if (_appliedKeys.Add(idempotencyKey))   // a replayed write with a known key is acknowledged, not applied again
        {
            _server.Add(text);
        }

        return Task.CompletedTask;
    }
}
