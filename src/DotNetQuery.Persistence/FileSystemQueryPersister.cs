using System.Text.Json;

namespace DotNetQuery.Persistence;

/// <summary>
/// A file-system <see cref="IQueryPersister"/> that stores each query as a JSON file in a directory.
/// Suitable for desktop and mobile apps (WPF, WinUI, MAUI, Avalonia).
/// </summary>
public sealed class FileSystemQueryPersister : IQueryPersister
{
    private const string Extension = ".dnq.json";
    private readonly string _directory;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>Creates a persister rooted at <paramref name="directory"/>, creating it if needed.</summary>
    public FileSystemQueryPersister(string directory, JsonSerializerOptions? jsonOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        _jsonOptions = jsonOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Directory.CreateDirectory(_directory);
    }

    /// <inheritdoc />
    public async Task SaveAsync(PersistedQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var path = PathForKey(query.Key);
        var json = JsonSerializer.Serialize(query, _jsonOptions);
        await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PersistedQuery?> LoadAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = PathForKey(key);
        if (!File.Exists(path))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<PersistedQuery>(json, _jsonOptions);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PersistedQuery>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<PersistedQuery>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*" + Extension))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            if (JsonSerializer.Deserialize<PersistedQuery>(json, _jsonOptions) is { } query)
            {
                results.Add(query);
            }
        }

        return results;
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = PathForKey(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        foreach (var path in Directory.EnumerateFiles(_directory, "*" + Extension))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string PathForKey(string key) => Path.Combine(_directory, HashFileName(key) + Extension);

    // Deterministic FNV-1a hash → stable, filesystem-safe file name for an arbitrary key string.
    private static string HashFileName(string key)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offset;
        foreach (var c in key)
        {
            hash ^= c;
            hash *= prime;
        }

        return hash.ToString("x16");
    }
}
