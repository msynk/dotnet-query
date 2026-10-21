using System.Collections.Concurrent;

namespace DotNetQuery.Persistence;

/// <summary>
/// An in-memory <see cref="IQueryPersister"/>. Useful for tests and for composing with a
/// write-through layer; does not survive process restarts.
/// </summary>
public sealed class InMemoryQueryPersister : IQueryPersister
{
    private readonly ConcurrentDictionary<string, PersistedQuery> _store = new();

    /// <inheritdoc />
    public Task SaveAsync(PersistedQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        _store[query.Key] = query;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<PersistedQuery?> LoadAsync(string key, CancellationToken cancellationToken = default)
    {
        _store.TryGetValue(key, out var query);
        return Task.FromResult(query);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PersistedQuery>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PersistedQuery> all = _store.Values.ToArray();
        return Task.FromResult(all);
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        _store.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        _store.Clear();
        return Task.CompletedTask;
    }
}
