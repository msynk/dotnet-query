using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotNetQuery.GraphQL;

namespace DotNetQuery;

/// <summary>
/// Convenience helpers for using a GraphQL-over-HTTP endpoint as a query fetcher. They POST a GraphQL
/// document (with optional variables), unwrap the <c>data</c> envelope, and surface any <c>errors</c> as
/// a <see cref="GraphQLException"/>. The core stays transport-agnostic; these are ergonomics.
/// </summary>
public static class GraphQLQueryExtensions
{
    private static readonly JsonSerializerOptions DefaultJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Builds a fetcher that POSTs <paramref name="query"/> (and optional <paramref name="variables"/>) to
    /// the GraphQL endpoint <paramref name="requestUri"/> and returns its <c>data</c> deserialized to
    /// <typeparamref name="T"/>. Throws <see cref="GraphQLException"/> if the response reports errors,
    /// <see cref="HttpRequestException"/> on a non-success status, and <see cref="InvalidOperationException"/>
    /// when the response has no data.
    /// </summary>
    /// <typeparam name="T">The shape of the GraphQL <c>data</c> payload.</typeparam>
    public static Func<CancellationToken, Task<T>> GraphQLFetcher<T>(
        this HttpClient client,
        string requestUri,
        string query,
        object? variables = null,
        JsonSerializerOptions? jsonOptions = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrEmpty(query);
        var options = jsonOptions ?? DefaultJson;
        return async cancellationToken =>
        {
            var request = new GraphQLRequest { Query = query, Variables = variables };
            using var response = await client.PostAsJsonAsync(requestUri, request, options, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content
                .ReadFromJsonAsync<GraphQLResponse<T>>(options, cancellationToken)
                .ConfigureAwait(false);

            if (payload is null)
            {
                throw new InvalidOperationException($"The GraphQL response from '{requestUri}' was empty.");
            }

            if (payload.Errors is { Count: > 0 })
            {
                throw new GraphQLException(payload.Errors);
            }

            return payload.Data ?? throw new InvalidOperationException(
                $"The GraphQL response from '{requestUri}' contained no data.");
        };
    }

    /// <summary>
    /// Fetches a GraphQL query into the cache under <paramref name="key"/>, using <paramref name="http"/>
    /// as the transport. A shorthand for
    /// <c>client.FetchAsync(key, http.GraphQLFetcher&lt;T&gt;(requestUri, query, variables, jsonOptions), options)</c>.
    /// </summary>
    public static Task<QueryResult<T>> FetchGraphQLAsync<T>(
        this IQueryClient client,
        QueryKey key,
        HttpClient http,
        string requestUri,
        string query,
        object? variables = null,
        QueryOptions? options = null,
        JsonSerializerOptions? jsonOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(http);
        return client.FetchAsync(
            key,
            http.GraphQLFetcher<T>(requestUri, query, variables, jsonOptions),
            options,
            cancellationToken: cancellationToken);
    }

    /// <summary>The request body sent to a GraphQL endpoint: <c>{ "query": ..., "variables": ... }</c>.</summary>
    private sealed class GraphQLRequest
    {
        public required string Query { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Variables { get; init; }
    }

    /// <summary>The GraphQL response envelope: <c>{ "data": ..., "errors": [...] }</c>.</summary>
    private sealed class GraphQLResponse<T>
    {
        public T? Data { get; init; }

        public IReadOnlyList<GraphQLError>? Errors { get; init; }
    }
}
