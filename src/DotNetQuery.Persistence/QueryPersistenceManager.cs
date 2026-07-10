using System.Collections.Concurrent;
using System.Text.Json;

namespace DotNetQuery.Persistence;

/// <summary>
/// Persists and restores query data between an <see cref="IQueryClient"/> and an
/// <see cref="IQueryPersister"/>.
/// </summary>
/// <remarks>
/// Register the query types you want persisted with <see cref="Register{T}"/>. Registration captures
/// the type in a closure, so no runtime generic instantiation (and no reflection over open generics)
/// is required. Restored data is written back into the client via <c>SetQueryData</c> and treated as
/// fresh from the moment of restore.
/// </remarks>
public sealed class QueryPersistenceManager
{
    private readonly IQueryClient _client;
    private readonly IQueryPersister _persister;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ConcurrentDictionary<string, Registration> _registrations = new();

    /// <summary>Creates a manager bound to a client and a backing persister.</summary>
    public QueryPersistenceManager(IQueryClient client, IQueryPersister persister, JsonSerializerOptions? jsonOptions = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _persister = persister ?? throw new ArgumentNullException(nameof(persister));
        _jsonOptions = jsonOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
    }

    /// <summary>
    /// Registers a query key for persistence with data type <typeparamref name="T"/>.
    /// </summary>
    public QueryPersistenceManager Register<T>(QueryKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var registration = new Registration(
            Key: key,
            Serialize: () =>
            {
                var result = _client.GetQueryData<T>(key);
                if (!result.State.HasData)
                {
                    return null;
                }

                var payload = JsonSerializer.Serialize(result.Data, _jsonOptions);
                return (payload, result.State.DataUpdatedAt ?? DateTimeOffset.UnixEpoch);
            },
            Apply: payload =>
            {
                var data = JsonSerializer.Deserialize<T>(payload, _jsonOptions);
                if (data is not null)
                {
                    _client.SetQueryData(key, data);
                }
            });

        _registrations[key.ToString()] = registration;
        return this;
    }

    /// <summary>Persists a single registered query's current data.</summary>
    public async Task PersistAsync(QueryKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (_registrations.TryGetValue(key.ToString(), out var registration))
        {
            await PersistRegistrationAsync(registration, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Persists the current data of every registered query that currently holds data.</summary>
    public async Task PersistAllAsync(CancellationToken cancellationToken = default)
    {
        foreach (var registration in _registrations.Values)
        {
            await PersistRegistrationAsync(registration, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Restores a single registered query from the persister into the client.
    /// </summary>
    /// <returns><see langword="true"/> if persisted data was found and applied.</returns>
    public async Task<bool> RestoreAsync(QueryKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!_registrations.TryGetValue(key.ToString(), out var registration))
        {
            return false;
        }

        var persisted = await _persister.LoadAsync(registration.Key.ToString(), cancellationToken).ConfigureAwait(false);
        if (persisted is null)
        {
            return false;
        }

        registration.Apply(persisted.Payload);
        return true;
    }

    /// <summary>Restores every registered query for which persisted data exists.</summary>
    /// <returns>The number of queries restored.</returns>
    public async Task<int> RestoreAllAsync(CancellationToken cancellationToken = default)
    {
        var persisted = await _persister.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        var byKey = persisted.ToDictionary(p => p.Key);
        var restored = 0;

        foreach (var registration in _registrations.Values)
        {
            if (byKey.TryGetValue(registration.Key.ToString(), out var entry))
            {
                registration.Apply(entry.Payload);
                restored++;
            }
        }

        return restored;
    }

    private async Task PersistRegistrationAsync(Registration registration, CancellationToken cancellationToken)
    {
        if (registration.Serialize() is not { } snapshot)
        {
            return;
        }

        await _persister.SaveAsync(
            new PersistedQuery(registration.Key.ToString(), snapshot.Payload, snapshot.UpdatedAt),
            cancellationToken).ConfigureAwait(false);
    }

    private sealed record Registration(
        QueryKey Key,
        Func<(string Payload, DateTimeOffset UpdatedAt)?> Serialize,
        Action<string> Apply);
}
