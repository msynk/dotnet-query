using System.Text.Json;
using Microsoft.JSInterop;

namespace DotNetQuery.Persistence;

/// <summary>
/// An <see cref="IQueryPersister"/> that stores query data in the browser's <c>localStorage</c> via
/// <see cref="IJSRuntime"/>, so cached data survives page reloads in Blazor WebAssembly. Also works on
/// Blazor Server (JS interop is remoted, with the usual latency).
/// </summary>
/// <remarks>
/// All persisted queries are kept together under a single <c>localStorage</c> entry (a JSON map keyed by
/// query key), so the whole set is read/written per operation. This suits the intended use — persisting a
/// bounded set of registered queries on shutdown and restoring them on startup — and needs no key
/// enumeration. Access is serialized with a semaphore to keep the read-modify-write of the map consistent.
/// </remarks>
public sealed class LocalStorageQueryPersister : IQueryPersister
{
    /// <summary>The default <c>localStorage</c> key under which the query map is stored.</summary>
    public const string DefaultStorageKey = "dotnetquery:queries";

    private readonly IJSRuntime _js;
    private readonly string _storageKey;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates a persister over <paramref name="jsRuntime"/>.</summary>
    /// <param name="jsRuntime">The Blazor JS runtime used to reach <c>localStorage</c>.</param>
    /// <param name="storageKey">The <c>localStorage</c> key to store under. Defaults to <see cref="DefaultStorageKey"/>.</param>
    /// <param name="jsonOptions">Serialization options for the stored map.</param>
    public LocalStorageQueryPersister(IJSRuntime jsRuntime, string? storageKey = null, JsonSerializerOptions? jsonOptions = null)
    {
        _js = jsRuntime ?? throw new ArgumentNullException(nameof(jsRuntime));
        _storageKey = storageKey ?? DefaultStorageKey;
        _jsonOptions = jsonOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
    }

    /// <inheritdoc />
    public async Task SaveAsync(PersistedQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var map = await ReadMapAsync(cancellationToken).ConfigureAwait(false);
            map[query.Key] = query;
            await WriteMapAsync(map, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<PersistedQuery?> LoadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var map = await ReadMapAsync(cancellationToken).ConfigureAwait(false);
            return map.GetValueOrDefault(key);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PersistedQuery>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var map = await ReadMapAsync(cancellationToken).ConfigureAwait(false);
            return map.Values.ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var map = await ReadMapAsync(cancellationToken).ConfigureAwait(false);
            if (map.Remove(key))
            {
                await WriteMapAsync(map, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _js.InvokeVoidAsync("localStorage.removeItem", cancellationToken, _storageKey).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, PersistedQuery>> ReadMapAsync(CancellationToken cancellationToken)
    {
        var json = await _js.InvokeAsync<string?>("localStorage.getItem", cancellationToken, _storageKey).ConfigureAwait(false);
        if (string.IsNullOrEmpty(json))
        {
            return new Dictionary<string, PersistedQuery>(StringComparer.Ordinal);
        }

        return JsonSerializer.Deserialize<Dictionary<string, PersistedQuery>>(json, _jsonOptions)
            ?? new Dictionary<string, PersistedQuery>(StringComparer.Ordinal);
    }

    private async Task WriteMapAsync(Dictionary<string, PersistedQuery> map, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(map, _jsonOptions);
        await _js.InvokeVoidAsync("localStorage.setItem", cancellationToken, _storageKey, json).ConfigureAwait(false);
    }
}
