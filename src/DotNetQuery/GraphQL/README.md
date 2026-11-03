# GraphQL fetchers

GraphQL-over-HTTP transport conveniences for DotNetQuery, shipped in the `DotNetQuery` package. The engine
is transport-agnostic; these helpers turn a GraphQL endpoint + document into a query fetcher - POSTing
`{ query, variables }`, unwrapping the `data` envelope, and surfacing any `errors` as a `GraphQLException`.

```csharp
using DotNetQuery;
using DotNetQuery.GraphQL;

// Shape matching the GraphQL `data` payload:
record UserResponse(UserDto User);
record UserDto(int Id, string Name);

// Build a fetcher and pass it to the client:
var fetcher = http.GraphQLFetcher<UserResponse>(
    "graphql",
    "query($id:Int!){ user(id:$id){ id name } }",
    variables: new { id = 7 });
await client.FetchAsync(QueryKey.Create<UserResponse>(7), fetcher);

// Or fetch straight into the cache:
var result = await client.FetchGraphQLAsync<UserResponse>(
    QueryKey.Create<UserResponse>(7), http, "graphql",
    "query($id:Int!){ user(id:$id){ id name } }", new { id = 7 });
```

## Mutations

`GraphQLMutationFn` turns a GraphQL mutation document into a mutation function: the mutation's variables object is
serialized as the GraphQL `variables`, and the `data` payload is returned. `CreateGraphQLMutation` wraps it in a
regular `Mutation<TVariables, TData>`, so optimistic updates, invalidation, retries, scopes and offline behavior work
exactly as for any other mutation.

```csharp
record AddTodoVariables(string Title);
record AddTodoData(TodoDto AddTodo);

[JsonSerializable(typeof(AddTodoVariables))]
[JsonSerializable(typeof(AddTodoData))]
partial class AppJson : JsonSerializerContext;

var addTodo = client.CreateGraphQLMutation(
    http, "graphql",
    "mutation($title:String!){ addTodo(title:$title){ id title } }",
    AppJson.Default.AddTodoVariables, AppJson.Default.AddTodoData,
    new() { Invalidates = [TodoKeys.All] });

await addTodo.MutateAsync(new AddTodoVariables("Buy milk"));
```

## Errors

Each `GraphQLError` carries the server's `Message`, the failing field's `Path` and the machine-readable
`extensions.code` as `Code`. `GraphQLException.HasCode("UNAUTHENTICATED")` makes retry decisions simple:

```csharp
options.DefaultShouldRetry = (attempt, error) =>
    error is not GraphQLException graphQL || !graphQL.HasCode("BAD_USER_INPUT") && !graphQL.HasCode("UNAUTHENTICATED");
```

## Behavior

- POSTs `{ "query": ..., "variables": ... }` (variables omitted when null) as JSON.
- A non-success HTTP status throws `HttpRequestException`; a populated `errors` array throws
  `GraphQLException` (its `Errors` list carries the messages); a null `data` throws
  `InvalidOperationException`.
- `T` is the shape of the `data` object. Pass a `JsonSerializerOptions` to control naming/converters; the
  default is `JsonSerializerDefaults.Web` (camelCase).
- **Trimming / NativeAOT:** the `JsonTypeInfo<T>` overloads (`GraphQLFetcher`, `FetchGraphQLAsync`,
  `GraphQLQuery`, `GraphQLMutationFn`, `CreateGraphQLMutation`) take variables as a `JsonObject` or a typed
  variables object with its `JsonTypeInfo` and deserialize `data` with source-generated metadata, so they are
  trim- and AOT-safe. The `object? variables` / `JsonSerializerOptions` overloads use reflection
  and are annotated `[RequiresUnreferencedCode]`.
