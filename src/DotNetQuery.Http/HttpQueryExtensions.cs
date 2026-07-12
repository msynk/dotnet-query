using System.Net.Http.Json;
using System.Text.Json;

namespace DotNetQuery;

/// <summary>
/// Convenience helpers for using an <see cref="HttpClient"/> as a query fetcher. The core is
/// transport-agnostic; these turn a URL into a JSON-deserializing <c>Func&lt;CancellationToken, Task&lt;T&gt;&gt;</c>
/// and let you fetch an endpoint straight into the cache.
/// </summary>
public static class HttpQueryExtensions
{
    /// <summary>
    /// Builds a fetcher that GETs <paramref name="requestUri"/> and deserializes the JSON response to
    /// <typeparamref name="T"/>. Throws <see cref="HttpRequestException"/> on a non-success status and
    /// <see cref="InvalidOperationException"/> if the body deserializes to <see langword="null"/>.
    /// </summary>
    public static Func<CancellationToken, Task<T>> JsonFetcher<T>(
        this HttpClient client,
        string requestUri,
        JsonSerializerOptions? jsonOptions = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(requestUri);
        return async cancellationToken =>
        {
            var result = await client.GetFromJsonAsync<T>(requestUri, jsonOptions, cancellationToken).ConfigureAwait(false);
            return result ?? throw new InvalidOperationException($"The response for '{requestUri}' deserialized to null.");
        };
    }

    /// <summary>
    /// Builds a fetcher that GETs <paramref name="requestUri"/> and deserializes the JSON response to
    /// <typeparamref name="T"/>.
    /// </summary>
    public static Func<CancellationToken, Task<T>> JsonFetcher<T>(
        this HttpClient client,
        Uri requestUri,
        JsonSerializerOptions? jsonOptions = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(requestUri);
        return async cancellationToken =>
        {
            var result = await client.GetFromJsonAsync<T>(requestUri, jsonOptions, cancellationToken).ConfigureAwait(false);
            return result ?? throw new InvalidOperationException($"The response for '{requestUri}' deserialized to null.");
        };
    }

    /// <summary>
    /// Fetches <paramref name="requestUri"/> as JSON into the cache under <paramref name="key"/>, using
    /// <paramref name="http"/> as the transport. A shorthand for
    /// <c>client.FetchAsync(key, http.JsonFetcher&lt;T&gt;(requestUri, jsonOptions), options)</c>.
    /// </summary>
    public static Task<QueryResult<T>> FetchJsonAsync<T>(
        this IQueryClient client,
        QueryKey key,
        HttpClient http,
        string requestUri,
        QueryOptions? options = null,
        JsonSerializerOptions? jsonOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(http);
        return client.FetchAsync(key, http.JsonFetcher<T>(requestUri, jsonOptions), options, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Prefetches <paramref name="requestUri"/> as JSON into the cache under <paramref name="key"/>
    /// (best-effort; errors are swallowed), useful for warming the cache ahead of navigation.
    /// </summary>
    public static Task PrefetchJsonAsync<T>(
        this IQueryClient client,
        QueryKey key,
        HttpClient http,
        string requestUri,
        QueryOptions? options = null,
        JsonSerializerOptions? jsonOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(http);
        return client.PrefetchAsync(key, http.JsonFetcher<T>(requestUri, jsonOptions), options, cancellationToken);
    }
}
