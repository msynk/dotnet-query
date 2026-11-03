using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using DotNetQuery.GraphQL;

namespace DotNetQuery;

/// <summary>
/// Convenience helpers for using a GraphQL-over-HTTP endpoint as a query fetcher. They POST a GraphQL
/// document (with optional variables), unwrap the <c>data</c> envelope, and surface any <c>errors</c> as
/// a <see cref="GraphQLException"/>. The core stays transport-agnostic; these are ergonomics.
/// </summary>
/// <remarks>
/// The <see cref="JsonTypeInfo{T}"/> overloads (variables as a <see cref="JsonObject"/>) are trim- and
/// NativeAOT-safe; the <see cref="JsonSerializerOptions"/> overloads serialize variables and data by reflection.
/// </remarks>
public static class GraphQLQueryExtensions
{
    private const string ReflectionMessage =
        "Reflection-based JSON serialization of the variables and T may require types that cannot be statically analyzed. Use the JsonTypeInfo<T> overload.";

    private const string DynamicCodeMessage =
        "Reflection-based JSON serialization of the variables and T may require runtime code generation. Use the JsonTypeInfo<T> overload.";

    private static readonly JsonSerializerOptions DefaultJson = new(JsonSerializerDefaults.Web);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions> DerivedOptions = new();
    private static readonly MediaTypeHeaderValue JsonContentType = new("application/json") { CharSet = "utf-8" };

    /// <summary>
    /// Builds a fetcher that POSTs <paramref name="query"/> (and optional <paramref name="variables"/>) to
    /// the GraphQL endpoint <paramref name="requestUri"/> and returns its <c>data</c> deserialized to
    /// <typeparamref name="T"/>. Throws <see cref="GraphQLException"/> if the response reports errors,
    /// <see cref="HttpRequestException"/> on a non-success status, and <see cref="InvalidOperationException"/>
    /// when the response has no data.
    /// </summary>
    /// <typeparam name="T">The shape of the GraphQL <c>data</c> payload.</typeparam>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(DynamicCodeMessage)]
    public static Func<CancellationToken, Task<T>> GraphQLFetcher<T>(
        this HttpClient client,
        string requestUri,
        string query,
        object? variables = null,
        JsonSerializerOptions? jsonOptions = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrEmpty(query);
        var options = WithResolver(jsonOptions ?? DefaultJson);
        var typeInfo = (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
        var variablesNode = variables is null ? null : JsonSerializer.SerializeToNode(variables, variables.GetType(), options);
        return Fetcher(client, requestUri, query, variablesNode, typeInfo);
    }

    /// <summary>
    /// Builds a trim/AOT-safe fetcher that POSTs <paramref name="query"/> (and optional
    /// <paramref name="variables"/>) to <paramref name="requestUri"/> and deserializes <c>data</c> with
    /// <paramref name="dataTypeInfo"/>. Errors behave as in the reflection-based overload.
    /// </summary>
    /// <typeparam name="T">The shape of the GraphQL <c>data</c> payload.</typeparam>
    public static Func<CancellationToken, Task<T>> GraphQLFetcher<T>(
        this HttpClient client,
        string requestUri,
        string query,
        JsonTypeInfo<T> dataTypeInfo,
        JsonObject? variables = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrEmpty(query);
        ArgumentNullException.ThrowIfNull(dataTypeInfo);
        return Fetcher(client, requestUri, query, variables?.DeepClone(), dataTypeInfo);
    }

    /// <summary>
    /// Fetches a GraphQL query into the cache under <paramref name="key"/>, using <paramref name="http"/>
    /// as the transport. A shorthand for
    /// <c>client.FetchAsync(key, http.GraphQLFetcher&lt;T&gt;(requestUri, query, variables, jsonOptions), options)</c>.
    /// </summary>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(DynamicCodeMessage)]
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

    /// <summary>
    /// Fetches a GraphQL query into the cache under <paramref name="key"/>, deserializing <c>data</c> with
    /// <paramref name="dataTypeInfo"/>. Trim/AOT-safe.
    /// </summary>
    public static Task<QueryResult<T>> FetchGraphQLAsync<T>(
        this IQueryClient client,
        QueryKey key,
        HttpClient http,
        string requestUri,
        string query,
        JsonTypeInfo<T> dataTypeInfo,
        JsonObject? variables = null,
        QueryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(http);
        return client.FetchAsync(
            key,
            http.GraphQLFetcher(requestUri, query, dataTypeInfo, variables),
            options,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Builds a trim/AOT-safe mutation function that POSTs the GraphQL <paramref name="mutation"/> document with the
    /// mutation's variables (serialized with <paramref name="variablesTypeInfo"/>) as the GraphQL <c>variables</c>, and
    /// returns its <c>data</c> deserialized with <paramref name="dataTypeInfo"/>. Errors behave as for queries: a
    /// populated <c>errors</c> array throws <see cref="GraphQLException"/>.
    /// </summary>
    /// <typeparam name="TVariables">The GraphQL variables object (for example <c>record AddTodo(string Title)</c>).</typeparam>
    /// <typeparam name="TData">The shape of the GraphQL <c>data</c> payload.</typeparam>
    /// <example>
    /// <code>
    /// var addTodo = client.CreateMutation(
    ///     http.GraphQLMutationFn("graphql", "mutation($title:String!){ addTodo(title:$title){ id title } }",
    ///         AppJson.Default.AddTodoVariables, AppJson.Default.AddTodoData),
    ///     new() { Invalidates = [TodoKeys.All] });
    /// </code>
    /// </example>
    public static Func<TVariables, CancellationToken, Task<TData>> GraphQLMutationFn<TVariables, TData>(
        this HttpClient client,
        string requestUri,
        string mutation,
        JsonTypeInfo<TVariables> variablesTypeInfo,
        JsonTypeInfo<TData> dataTypeInfo)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrEmpty(mutation);
        ArgumentNullException.ThrowIfNull(variablesTypeInfo);
        ArgumentNullException.ThrowIfNull(dataTypeInfo);
        return (variables, cancellationToken) => SendAsync(
            client,
            requestUri,
            BuildRequestBody(mutation, writer => JsonSerializer.Serialize(writer, variables, variablesTypeInfo)),
            dataTypeInfo,
            cancellationToken);
    }

    /// <summary>
    /// Builds a mutation function that POSTs the GraphQL <paramref name="mutation"/> document with the mutation's
    /// variables serialized as the GraphQL <c>variables</c> by reflection, and returns its <c>data</c> deserialized to
    /// <typeparamref name="TData"/>.
    /// </summary>
    /// <typeparam name="TVariables">The GraphQL variables object.</typeparam>
    /// <typeparam name="TData">The shape of the GraphQL <c>data</c> payload.</typeparam>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(DynamicCodeMessage)]
    public static Func<TVariables, CancellationToken, Task<TData>> GraphQLMutationFn<TVariables, TData>(
        this HttpClient client,
        string requestUri,
        string mutation,
        JsonSerializerOptions? jsonOptions = null)
    {
        var options = WithResolver(jsonOptions ?? DefaultJson);
        return client.GraphQLMutationFn(
            requestUri,
            mutation,
            (JsonTypeInfo<TVariables>)options.GetTypeInfo(typeof(TVariables)),
            (JsonTypeInfo<TData>)options.GetTypeInfo(typeof(TData)));
    }

    /// <summary>
    /// Creates a <see cref="Mutation{TVariables, TData}"/> for a GraphQL mutation (see
    /// <see cref="GraphQLMutationFn{TVariables, TData}(HttpClient, string, string, JsonTypeInfo{TVariables}, JsonTypeInfo{TData})"/>),
    /// with the usual optimistic updates, invalidation, retries and offline behavior. Trim/AOT-safe.
    /// </summary>
    public static Mutation<TVariables, TData> CreateGraphQLMutation<TVariables, TData>(
        this IQueryClient client,
        HttpClient http,
        string requestUri,
        string mutation,
        JsonTypeInfo<TVariables> variablesTypeInfo,
        JsonTypeInfo<TData> dataTypeInfo,
        MutationOptions<TVariables, TData>? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(http);
        return client.CreateMutation(http.GraphQLMutationFn(requestUri, mutation, variablesTypeInfo, dataTypeInfo), options);
    }

    /// <summary>
    /// Creates a reusable <see cref="QueryDefinition{T}"/> for a GraphQL query. Trim/AOT-safe.
    /// </summary>
    public static QueryDefinition<T> GraphQLQuery<T>(
        this HttpClient client,
        QueryKey key,
        string requestUri,
        string query,
        JsonTypeInfo<T> dataTypeInfo,
        JsonObject? variables = null,
        QueryOptions? options = null) =>
        new(key, client.GraphQLFetcher(requestUri, query, dataTypeInfo, variables), options);

    // GetTypeInfo needs a resolver; never mutate the caller's options, derive (and cache) a copy instead.
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(DynamicCodeMessage)]
    private static JsonSerializerOptions WithResolver(JsonSerializerOptions options) =>
        options.TypeInfoResolver is not null
            ? options
            : DerivedOptions.GetValue(options, static o => new JsonSerializerOptions(o) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() });

    private static Func<CancellationToken, Task<T>> Fetcher<T>(
        HttpClient client,
        string requestUri,
        string query,
        JsonNode? variables,
        JsonTypeInfo<T> dataTypeInfo)
    {
        var body = BuildRequestBody(query, variables is null ? null : writer => variables.WriteTo(writer));
        return cancellationToken => SendAsync(client, requestUri, body, dataTypeInfo, cancellationToken);
    }

    private static async Task<T> SendAsync<T>(HttpClient client, string requestUri, byte[] body, JsonTypeInfo<T> dataTypeInfo, CancellationToken cancellationToken)
    {
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = JsonContentType;
        using var response = await client.PostAsync(requestUri, content, cancellationToken).ConfigureAwait(false);
        HttpRetryPolicy.EnsureSuccess(response);

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return ReadData(document.RootElement, requestUri, dataTypeInfo);
        }
    }

    private static byte[] BuildRequestBody(string query, Action<Utf8JsonWriter>? writeVariables)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("query", query);
            if (writeVariables is not null)
            {
                writer.WritePropertyName("variables");
                writeVariables(writer);
            }

            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static T ReadData<T>(JsonElement root, string requestUri, JsonTypeInfo<T> dataTypeInfo)
    {
        if (root.ValueKind is not JsonValueKind.Object)
        {
            throw new InvalidOperationException($"The GraphQL response from '{requestUri}' was empty.");
        }

        if (TryGetProperty(root, "errors", out var errors) && errors.ValueKind is JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            var list = new List<GraphQLError>(errors.GetArrayLength());
            foreach (var error in errors.EnumerateArray())
            {
                list.Add(ReadError(error));
            }

            throw new GraphQLException(list);
        }

        if (!TryGetProperty(root, "data", out var data) || data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new InvalidOperationException($"The GraphQL response from '{requestUri}' contained no data.");
        }

        return data.Deserialize(dataTypeInfo)
            ?? throw new InvalidOperationException($"The GraphQL response from '{requestUri}' contained no data.");
    }

    private static GraphQLError ReadError(JsonElement error)
    {
        if (error.ValueKind is not JsonValueKind.Object)
        {
            return new GraphQLError(error.ToString());
        }

        var message = TryGetProperty(error, "message", out var m) && m.ValueKind is JsonValueKind.String ? m.GetString()! : error.ToString();
        IReadOnlyList<string>? path = TryGetProperty(error, "path", out var p) && p.ValueKind is JsonValueKind.Array
            ? p.EnumerateArray().Select(segment => segment.ValueKind is JsonValueKind.String ? segment.GetString()! : segment.ToString()).ToArray()
            : null;
        var code = TryGetProperty(error, "extensions", out var extensions)
            && extensions.ValueKind is JsonValueKind.Object
            && TryGetProperty(extensions, "code", out var c)
            && c.ValueKind is JsonValueKind.String
                ? c.GetString()
                : null;
        return new GraphQLError(message) { Path = path, Code = code };
    }

    // GraphQL property names are lower-case by spec, but tolerate other casings.
    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }
}
