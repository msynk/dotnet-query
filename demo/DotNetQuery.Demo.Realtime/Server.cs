using DotNetQuery.Synchronization;
using Microsoft.AspNetCore.SignalR;

namespace DotNetQuery.Demo.Realtime;

public sealed record Todo(int Id, string Title, bool Done, long Version);

public sealed record TodoStats(int Total, int Done);

/// <summary>
/// The server's source of truth: versioned todos and a retained, sequenced event history (so clients can replay what
/// they missed). Every change is broadcast to the workspace's SignalR group as a <see cref="QueryEventEnvelope{TPayload}"/>.
/// </summary>
public sealed class TodoStore(IHubContext<TodoHub> hub)
{
    public const string Workspace = "acme";

    private readonly object _gate = new();
    private readonly Dictionary<int, Todo> _todos = new()
    {
        [1] = new(1, "Write the spec", false, 1),
        [2] = new(2, "Review the PR", false, 1),
        [3] = new(3, "Ship it", false, 1),
    };

    private readonly List<QueryEventEnvelope<Todo>> _history = [];
    private long _sequence;

    public List<Todo> All()
    {
        lock (_gate)
        {
            return [.. _todos.Values.OrderBy(t => t.Id)];
        }
    }

    public Todo? Get(int id)
    {
        lock (_gate)
        {
            return _todos.GetValueOrDefault(id);
        }
    }

    public TodoStats Stats()
    {
        lock (_gate)
        {
            return new TodoStats(_todos.Count, _todos.Values.Count(t => t.Done));
        }
    }

    /// <summary>Applies a change, records it in the history and broadcasts it - optionally without delivering it (a dropped message).</summary>
    public async Task<QueryEventEnvelope<Todo>> ChangeAsync(int id, Func<Todo, Todo> change, string? correlationId = null)
    {
        QueryEventEnvelope<Todo> envelope;
        lock (_gate)
        {
            var current = _todos[id];
            var updated = change(current) with { Version = current.Version + 1 };
            _todos[id] = updated;
            envelope = new QueryEventEnvelope<Todo>
            {
                EventId = Guid.NewGuid().ToString("N"),
                EventType = "TodoChanged",
                EntityId = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Version = updated.Version,
                Sequence = ++_sequence,
                Scope = Workspace,
                CorrelationId = correlationId,
                Timestamp = DateTimeOffset.UtcNow,
                Payload = updated,
            };
            _history.Add(envelope);
        }

        await BroadcastAsync(envelope);
        return envelope;
    }

    public Task BroadcastAsync(QueryEventEnvelope<Todo> envelope) =>
        hub.Clients.Group(Workspace).SendAsync("TodoChanged", envelope);

    public List<QueryEventEnvelope<Todo>> Since(long sequence)
    {
        lock (_gate)
        {
            return _history.Where(e => e.Sequence > sequence).ToList();
        }
    }
}

public sealed class TodoHub(TodoStore store) : Hub
{
    /// <summary>Group memberships belong to a connection: clients call this on every (re)connect.</summary>
    public Task JoinWorkspace(string workspace) => Groups.AddToGroupAsync(Context.ConnectionId, workspace);

    /// <summary>The replay endpoint: every event after <paramref name="sequence"/> the server still retains.</summary>
    public List<QueryEventEnvelope<Todo>> EventsSince(long sequence) => store.Since(sequence);
}
