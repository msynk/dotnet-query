using System.Text.Json;
using Microsoft.JSInterop;

namespace DotNetQuery.Persistence;

/// <summary>
/// An <see cref="IQueryPersister"/> that stores query data in the browser's IndexedDB via
/// <see cref="IJSRuntime"/>, for datasets larger than <c>localStorage</c>'s ~5&#160;MB quota. Each query is
/// stored as a JSON string keyed by its query key.
/// </summary>
/// <remarks>
/// Requires the bundled JS module to be referenced by the Blazor host page:
/// <code>&lt;script src="_content/DotNetQuery.Persistence.BlazorWasm/dotnetquery-indexeddb.js"&gt;&lt;/script&gt;</code>
/// Call it only where JS interop is available (e.g. after first render in WebAssembly), not during
/// prerender.
/// </remarks>
public sealed class IndexedDbQueryPersister : IQueryPersister
{
    /// <summary>The default IndexedDB database name.</summary>
    public const string DefaultDatabaseName = "dotnetquery";

    /// <summary>The default IndexedDB object-store name.</summary>
    public const string DefaultStoreName = "queries";

    private readonly IJSRuntime _js;
    private readonly string _databaseName;
    private readonly string _storeName;
    private readonly JsonSerializerOptions? _jsonOptions;

    /// <summary>Creates a persister over <paramref name="jsRuntime"/>.</summary>
    /// <param name="jsRuntime">The Blazor JS runtime used to reach IndexedDB.</param>
    /// <param name="databaseName">The IndexedDB database. Defaults to <see cref="DefaultDatabaseName"/>.</param>
    /// <param name="storeName">The object store. Defaults to <see cref="DefaultStoreName"/>.</param>
    /// <param name="jsonOptions">Serialization options for the stored records.</param>
    public IndexedDbQueryPersister(
        IJSRuntime jsRuntime,
        string? databaseName = null,
        string? storeName = null,
        JsonSerializerOptions? jsonOptions = null)
    {
        _js = jsRuntime ?? throw new ArgumentNullException(nameof(jsRuntime));
        _databaseName = databaseName ?? DefaultDatabaseName;
        _storeName = storeName ?? DefaultStoreName;
        _jsonOptions = jsonOptions;
    }

    /// <inheritdoc />
    public async Task SaveAsync(PersistedQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var json = PersistedQueryJson.Serialize(query, _jsonOptions);
        await _js.InvokeVoidAsync("dotnetQueryIndexedDb.save", cancellationToken, _databaseName, _storeName, query.Key, json).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PersistedQuery?> LoadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var json = await _js.InvokeAsync<string?>("dotnetQueryIndexedDb.load", cancellationToken, _databaseName, _storeName, key).ConfigureAwait(false);
        return string.IsNullOrEmpty(json) ? null : PersistedQueryJson.Deserialize(json, _jsonOptions);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PersistedQuery>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        var jsons = await _js.InvokeAsync<string[]>("dotnetQueryIndexedDb.loadAll", cancellationToken, _databaseName, _storeName).ConfigureAwait(false);
        var results = new List<PersistedQuery>(jsons.Length);
        foreach (var json in jsons)
        {
            if (string.IsNullOrEmpty(json))
            {
                continue;
            }

            try
            {
                if (PersistedQueryJson.Deserialize(json, _jsonOptions) is { } query)
                {
                    results.Add(query);
                }
            }
            catch (JsonException)
            {
                // Skip corrupt or incompatible records instead of failing the whole restore.
            }
        }

        return results;
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await _js.InvokeVoidAsync("dotnetQueryIndexedDb.remove", cancellationToken, _databaseName, _storeName, key).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _js.InvokeVoidAsync("dotnetQueryIndexedDb.clear", cancellationToken, _databaseName, _storeName).ConfigureAwait(false);
    }
}
