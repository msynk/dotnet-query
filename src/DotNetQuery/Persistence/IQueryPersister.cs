namespace DotNetQuery.Persistence;

/// <summary>
/// A single persisted query: its string key, a serialized payload, and the time its data was last updated.
/// </summary>
/// <param name="Key">The stable string form of the query key.</param>
/// <param name="Payload">The serialized query data.</param>
/// <param name="UpdatedAt">The UTC time the data was last updated when persisted.</param>
public sealed record PersistedQuery(string Key, string Payload, DateTimeOffset UpdatedAt);

/// <summary>
/// A pluggable backing store for persisted query data. Implementations may target memory, the file
/// system, local storage, IndexedDB, SQLite, Redis, and so on.
/// </summary>
public interface IQueryPersister
{
    /// <summary>Persists (inserts or replaces) a single query.</summary>
    Task SaveAsync(PersistedQuery query, CancellationToken cancellationToken = default);

    /// <summary>Loads a single persisted query by key, or <see langword="null"/> if absent.</summary>
    Task<PersistedQuery?> LoadAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Loads every persisted query.</summary>
    Task<IReadOnlyList<PersistedQuery>> LoadAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes a single persisted query by key.</summary>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Removes every persisted query.</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}
