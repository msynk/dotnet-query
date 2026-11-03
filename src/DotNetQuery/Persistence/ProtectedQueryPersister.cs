namespace DotNetQuery.Persistence;

/// <summary>
/// Encrypts (or otherwise transforms) payloads on their way into any <see cref="IQueryPersister"/> and back - the hook
/// for keeping sensitive cached data and queued mutation variables unreadable at rest (local storage, IndexedDB, a
/// file, SQLite, Redis). Keys and timestamps are stored as-is.
/// </summary>
/// <example>
/// <code>
/// // ASP.NET Core Data Protection (or any symmetric encryption you trust):
/// var protector = dataProtectionProvider.CreateProtector("DotNetQuery.Cache.v1");
/// var persister = new ProtectedQueryPersister(new FileSystemQueryPersister(dir), protector.Protect, protector.Unprotect);
/// </code>
/// </example>
/// <remarks>
/// A record that cannot be unprotected (wrong or rotated key, tampering, data written before protection was
/// enabled) is skipped - reported through <see cref="Error"/> and treated as absent - so a key change degrades to
/// refetching instead of failing startup.
/// </remarks>
public sealed class ProtectedQueryPersister : IQueryPersister
{
    private readonly IQueryPersister _inner;
    private readonly Func<string, string> _protect;
    private readonly Func<string, string> _unprotect;

    /// <summary>Wraps <paramref name="inner"/>, applying <paramref name="protect"/> before saving and <paramref name="unprotect"/> after loading.</summary>
    public ProtectedQueryPersister(IQueryPersister inner, Func<string, string> protect, Func<string, string> unprotect)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _protect = protect ?? throw new ArgumentNullException(nameof(protect));
        _unprotect = unprotect ?? throw new ArgumentNullException(nameof(unprotect));
    }

    /// <summary>Raised with the storage key when a stored record cannot be unprotected; the record is treated as absent.</summary>
    public event Action<string, Exception>? Error;

    /// <inheritdoc />
    public Task SaveAsync(PersistedQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return _inner.SaveAsync(query with { Payload = _protect(query.Payload) }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<PersistedQuery?> LoadAsync(string key, CancellationToken cancellationToken = default)
    {
        var stored = await _inner.LoadAsync(key, cancellationToken).ConfigureAwait(false);
        return stored is null ? null : TryUnprotect(stored);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PersistedQuery>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _inner.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<PersistedQuery>(stored.Count);
        foreach (var record in stored)
        {
            if (TryUnprotect(record) is { } plain)
            {
                result.Add(plain);
            }
        }

        return result;
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default) => _inner.RemoveAsync(key, cancellationToken);

    /// <inheritdoc />
    public Task ClearAsync(CancellationToken cancellationToken = default) => _inner.ClearAsync(cancellationToken);

    private PersistedQuery? TryUnprotect(PersistedQuery stored)
    {
        try
        {
            return stored with { Payload = _unprotect(stored.Payload) };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try
            {
                Error?.Invoke(stored.Key, ex);
            }
            catch (Exception)
            {
                // Subscriber errors must not break loading.
            }

            return null;
        }
    }
}
