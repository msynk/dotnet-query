# DotNetQuery.GraphQL

GraphQL-over-HTTP transport conveniences for DotNetQuery. The core is transport-agnostic; these helpers
turn a GraphQL endpoint + document into a query fetcher - POSTing `{ query, variables }`, unwrapping the
`data` envelope, and surfacing any `errors` as a `GraphQLException`.

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

## Behavior

- POSTs `{ "query": ..., "variables": ... }` (variables omitted when null) as JSON.
- A non-success HTTP status throws `HttpRequestException`; a populated `errors` array throws
  `GraphQLException` (its `Errors` list carries the messages); a null `data` throws
  `InvalidOperationException`.
- `T` is the shape of the `data` object. Pass a `JsonSerializerOptions` to control naming/converters; the
  default is `JsonSerializerDefaults.Web` (camelCase).
- Uses reflection-based `System.Text.Json`, so this package is **not** AOT/trim-safe.
