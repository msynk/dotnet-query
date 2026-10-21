using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace DotNetQuery;

/// <summary>
/// Convenience helpers for using an <see cref="HttpClient"/> as a query fetcher. The core is
/// transport-agnostic; these turn a URL into a JSON-deserializing <c>Func&lt;CancellationToken, Task&lt;T&gt;&gt;</c>
/// (or a whole <see cref="QueryDefinition{T}"/>) and let you fetch an endpoint straight into the cache.
/// </summary>
/// <remarks>
/// Every helper has a <see cref="JsonTypeInfo{T}"/> overload (typically from a source-generated
/// <c>JsonSerializerContext</c>) that is trim- and NativeAOT-safe - prefer it in Blazor WebAssembly and other
/// trimmed apps. The <see cref="JsonSerializerOptions"/> overloads use reflection-based serialization.
/// </remarks>
public static class HttpQueryExtensions
{
    private const string ReflectionMessage =
        "Reflection-based JSON deserialization of T may require types that cannot be statically analyzed. Use the JsonTypeInfo<T> overload.";

    private const string DynamicCodeMessage =
        "Reflection-based JSON deserialization of T may require runtime code generation. Use the JsonTypeInfo<T> overload.";

    /// <summary>
    /// Builds a fetcher that GETs <paramref name="requestUri"/> and deserializes the JSON response to
    /// <typeparamref name="T"/>. Throws <see cref="HttpRequestException"/> on a non-success status and
    /// <see cref="InvalidOperationException"/> if the body deserializes to <see langword="null"/>.
    /// </summary>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(DynamicCodeMessage)]
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
            return result ?? throw NullBody(requestUri);
        };
    }

    /// <summary>
    /// Builds a fetcher that GETs <paramref name="requestUri"/> and deserializes the JSON response to
    /// <typeparamref name="T"/>.
    /// </summary>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(DynamicCodeMessage)]
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
            return result ?? throw NullBody(requestUri.ToString());
        };
    }

    /// <summary>
    /// Builds a trim/AOT-safe fetcher that GETs <paramref name="requestUri"/> and deserializes the JSON
    /// response with <paramref name="typeInfo"/>.
    /// </summary>
    public static Func<CancellationToken, Task<T>> JsonFetcher<T>(
        this HttpClient client,
        string requestUri,
        JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(requestUri);
        ArgumentNullException.ThrowIfNull(typeInfo);
        return async cancellationToken =>
        {
            var result = await client.GetFromJsonAsync(requestUri, typeInfo, cancellationToken).ConfigureAwait(false);
            return result ?? throw NullBody(requestUri);
        };
    }

    /// <summary>
    /// Builds a trim/AOT-safe fetcher that GETs <paramref name="requestUri"/> and deserializes the JSON
    /// response with <paramref name="typeInfo"/>.
    /// </summary>
    public static Func<CancellationToken, Task<T>> JsonFetcher<T>(
        this HttpClient client,
        Uri requestUri,
        JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(requestUri);
        ArgumentNullException.ThrowIfNull(typeInfo);
        return async cancellationToken =>
        {
            var result = await client.GetFromJsonAsync(requestUri, typeInfo, cancellationToken).ConfigureAwait(false);
            return result ?? throw NullBody(requestUri.ToString());
        };
    }

    /// <summary>
    /// Creates a reusable <see cref="QueryDefinition{T}"/> that GETs <paramref name="requestUri"/> as JSON,
    /// e.g. <c>http.JsonQuery(TodoKeys.ById(7), "todos/7", AppJson.Default.Todo)</c>. Trim/AOT-safe.
    /// </summary>
    public static QueryDefinition<T> JsonQuery<T>(
        this HttpClient client,
        QueryKey key,
        string requestUri,
        JsonTypeInfo<T> typeInfo,
        QueryOptions? options = null) =>
        new(key, client.JsonFetcher(requestUri, typeInfo), options);

    /// <summary>
    /// Creates a reusable <see cref="QueryDefinition{T}"/> that GETs <paramref name="requestUri"/> as JSON
    /// (reflection-based serialization).
    /// </summary>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(DynamicCodeMessage)]
    public static QueryDefinition<T> JsonQuery<T>(
        this HttpClient client,
        QueryKey key,
        string requestUri,
        QueryOptions? options = null,
        JsonSerializerOptions? jsonOptions = null) =>
        new(key, client.JsonFetcher<T>(requestUri, jsonOptions), options);

    /// <summary>
    /// Fetches <paramref name="requestUri"/> as JSON into the cache under <paramref name="key"/>, using
    /// <paramref name="http"/> as the transport. A shorthand for
    /// <c>client.FetchAsync(key, http.JsonFetcher&lt;T&gt;(requestUri, jsonOptions), options)</c>.
    /// </summary>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(DynamicCodeMessage)]
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
    /// Fetches <paramref name="requestUri"/> as JSON into the cache under <paramref name="key"/>, deserialized
    /// with <paramref name="typeInfo"/>. Trim/AOT-safe.
    /// </summary>
    public static Task<QueryResult<T>> FetchJsonAsync<T>(
        this IQueryClient client,
        QueryKey key,
        HttpClient http,
        string requestUri,
        JsonTypeInfo<T> typeInfo,
        QueryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(http);
        return client.FetchAsync(key, http.JsonFetcher(requestUri, typeInfo), options, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Prefetches <paramref name="requestUri"/> as JSON into the cache under <paramref name="key"/>
    /// (best-effort; errors are swallowed), useful for warming the cache ahead of navigation.
    /// </summary>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(DynamicCodeMessage)]
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

    /// <summary>
    /// Prefetches <paramref name="requestUri"/> as JSON into the cache under <paramref name="key"/>,
    /// deserialized with <paramref name="typeInfo"/> (best-effort; errors are swallowed). Trim/AOT-safe.
    /// </summary>
    public static Task PrefetchJsonAsync<T>(
        this IQueryClient client,
        QueryKey key,
        HttpClient http,
        string requestUri,
        JsonTypeInfo<T> typeInfo,
        QueryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(http);
        return client.PrefetchAsync(key, http.JsonFetcher(requestUri, typeInfo), options, cancellationToken);
    }

    private static InvalidOperationException NullBody(string requestUri) =>
        new($"The response for '{requestUri}' deserialized to null.");
}
