using System.Text.Json;

namespace DotNetQuery.Persistence;

/// <summary>
/// A file-system <see cref="IQueryPersister"/> that stores each query as a JSON file in a directory.
/// Suitable for desktop and mobile apps (WPF, WinUI, MAUI, Avalonia).
/// </summary>
/// <remarks>
/// Writes are atomic (a temporary file is written and then moved into place), so a crash mid-write never
/// leaves a truncated entry behind. Unreadable files - for example from an older, incompatible version -
/// are skipped when loading instead of failing the whole restore.
/// </remarks>
public sealed class FileSystemQueryPersister : IQueryPersister
{
    private const string Extension = ".dnq.json";
    private readonly string _directory;
    private readonly JsonSerializerOptions? _jsonOptions;

    /// <summary>Creates a persister rooted at <paramref name="directory"/>, creating it if needed.</summary>
    public FileSystemQueryPersister(string directory, JsonSerializerOptions? jsonOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        _jsonOptions = jsonOptions;
        Directory.CreateDirectory(_directory);
    }

    /// <inheritdoc />
    public async Task SaveAsync(PersistedQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var path = PathForKey(query.Key);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var json = PersistedQueryJson.Serialize(query, _jsonOptions);
        try
        {
            await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    /// <inheritdoc />
    public async Task<PersistedQuery?> LoadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var query = await ReadAsync(PathForKey(key), cancellationToken).ConfigureAwait(false);

        // Guard against a (vanishingly unlikely) file-name hash collision.
        return query is not null && string.Equals(query.Key, key, StringComparison.Ordinal) ? query : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PersistedQuery>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<PersistedQuery>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*" + Extension))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await ReadAsync(path, cancellationToken).ConfigureAwait(false) is { } query)
            {
                results.Add(query);
            }
        }

        return results;
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
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

    private async Task<PersistedQuery?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        string json;
        try
        {
            json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null; // removed concurrently
        }

        try
        {
            return PersistedQueryJson.Deserialize(json, _jsonOptions);
        }
        catch (JsonException)
        {
            return null; // corrupt or incompatible entry: skip it
        }
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
