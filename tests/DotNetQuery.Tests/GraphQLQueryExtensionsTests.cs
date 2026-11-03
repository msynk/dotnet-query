using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotNetQuery;
using DotNetQuery.GraphQL;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Tests;

internal sealed record AddTodoVariables(string Title);

internal sealed record AddTodoData(AddedTodo AddTodo);

internal sealed record AddedTodo(int Id, string Title);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(AddTodoVariables))]
[JsonSerializable(typeof(AddTodoData))]
internal sealed partial class GraphQLTestJson : JsonSerializerContext;

public class GraphQLQueryExtensionsTests
{
    private sealed record UserResponse(UserDto User);
    private sealed record UserDto(int Id, string Name);

    private static QueryClient CreateClient() => new(new QueryClientOptions(), new FakeTimeProvider());

    private static CapturingHandler Handler(Func<HttpResponseMessage> responder, out HttpClient http)
    {
        var handler = new CapturingHandler(responder);
        http = new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") };
        return handler;
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task GraphQLFetcher_unwraps_the_data_envelope()
    {
        Handler(() => Json("{\"data\":{\"user\":{\"id\":7,\"name\":\"Ada\"}}}"), out var http);
        var fetcher = http.GraphQLFetcher<UserResponse>("graphql", "query { user { id name } }");

        var result = await fetcher(CancellationToken.None);

        Assert.Equal(new UserDto(7, "Ada"), result.User);
    }

    [Fact]
    public async Task GraphQLFetcher_sends_query_and_variables_in_the_body()
    {
        var handler = Handler(() => Json("{\"data\":{\"user\":{\"id\":7,\"name\":\"Ada\"}}}"), out var http);
        var fetcher = http.GraphQLFetcher<UserResponse>("graphql", "query($id:Int!){ user(id:$id){ id name } }", new { id = 7 });

        await fetcher(CancellationToken.None);

        Assert.NotNull(handler.LastBody);
        Assert.Contains("\"query\"", handler.LastBody!);
        Assert.Contains("user(id:$id)", handler.LastBody);
        Assert.Contains("\"variables\":{\"id\":7}", handler.LastBody);
    }

    [Fact]
    public async Task GraphQLFetcher_throws_GraphQLException_on_errors()
    {
        Handler(() => Json("{\"errors\":[{\"message\":\"boom\"},{\"message\":\"bad id\"}]}"), out var http);
        var fetcher = http.GraphQLFetcher<UserResponse>("graphql", "query { user { id } }");

        var ex = await Assert.ThrowsAsync<GraphQLException>(() => fetcher(CancellationToken.None));
        Assert.Equal(2, ex.Errors.Count);
        Assert.Contains("boom", ex.Message);
        Assert.Contains("bad id", ex.Message);
    }

    [Fact]
    public async Task GraphQLFetcher_throws_when_data_is_null()
    {
        Handler(() => Json("{\"data\":null}"), out var http);
        var fetcher = http.GraphQLFetcher<UserResponse>("graphql", "query { user { id } }");

        await Assert.ThrowsAsync<InvalidOperationException>(() => fetcher(CancellationToken.None));
    }

    [Fact]
    public async Task GraphQLFetcher_throws_on_non_success_status()
    {
        Handler(() => new HttpResponseMessage(HttpStatusCode.InternalServerError), out var http);
        var fetcher = http.GraphQLFetcher<UserResponse>("graphql", "query { user { id } }");

        await Assert.ThrowsAsync<HttpRequestException>(() => fetcher(CancellationToken.None));
    }

    [Fact]
    public async Task FetchGraphQLAsync_populates_the_cache()
    {
        using var client = CreateClient();
        Handler(() => Json("{\"data\":{\"user\":{\"id\":7,\"name\":\"Ada\"}}}"), out var http);
        var key = QueryKey.Create<UserResponse>(7);

        var result = await client.FetchGraphQLAsync<UserResponse>(key, http, "graphql", "query { user { id name } }");

        Assert.True(result.IsSuccess);
        Assert.Equal(new UserDto(7, "Ada"), result.Data!.User);
        Assert.Equal("Ada", client.GetQueryData<UserResponse>(key).Data!.User.Name);
    }

    private const string AddTodo = "mutation($title:String!){ addTodo(title:$title){ id title } }";

    [Fact]
    public async Task GraphQLMutationFn_sends_the_variables_and_returns_the_data()
    {
        var handler = Handler(() => Json("{\"data\":{\"addTodo\":{\"id\":3,\"title\":\"Milk\"}}}"), out var http);
        var mutate = http.GraphQLMutationFn("graphql", AddTodo, GraphQLTestJson.Default.AddTodoVariables, GraphQLTestJson.Default.AddTodoData);

        var data = await mutate(new AddTodoVariables("Milk"), CancellationToken.None);

        Assert.Equal(new AddedTodo(3, "Milk"), data.AddTodo);
        Assert.Contains("addTodo(title:$title)", handler.LastBody);
        Assert.Contains("\"variables\":{\"title\":\"Milk\"}", handler.LastBody);
    }

    [Fact]
    public async Task GraphQLMutationFn_reflection_overload_serializes_with_web_defaults()
    {
        var handler = Handler(() => Json("{\"data\":{\"addTodo\":{\"id\":4,\"title\":\"Eggs\"}}}"), out var http);
        var mutate = http.GraphQLMutationFn<AddTodoVariables, AddTodoData>("graphql", AddTodo);

        var data = await mutate(new AddTodoVariables("Eggs"), CancellationToken.None);

        Assert.Equal(4, data.AddTodo.Id);
        Assert.Contains("\"variables\":{\"title\":\"Eggs\"}", handler.LastBody);
    }

    [Fact]
    public async Task CreateGraphQLMutation_runs_through_the_mutation_pipeline()
    {
        using var client = new QueryClient(new QueryClientOptions { DefaultStaleTime = TimeSpan.FromMinutes(5) }, new FakeTimeProvider());
        Handler(() => Json("{\"data\":{\"addTodo\":{\"id\":5,\"title\":\"Tea\"}}}"), out var http);
        var todos = QueryKey.Create("todos");
        await client.FetchAsync(todos, _ => Task.FromResult(new List<string>()));
        Assert.False(client.Cache.Describe(todos)!.IsStale);
        var mutation = client.CreateGraphQLMutation(
            http,
            "graphql",
            AddTodo,
            GraphQLTestJson.Default.AddTodoVariables,
            GraphQLTestJson.Default.AddTodoData,
            new MutationOptions<AddTodoVariables, AddTodoData> { Invalidates = [todos] });

        var result = await mutation.MutateAsync(new AddTodoVariables("Tea"));

        Assert.True(result.IsSuccess);
        Assert.Equal("Tea", result.Data!.AddTodo.Title);
        Assert.True(client.Cache.Describe(todos)!.IsStale); // invalidated by the mutation
    }

    [Fact]
    public async Task GraphQL_errors_carry_their_path_and_code()
    {
        Handler(() => Json("{\"errors\":[{\"message\":\"Sign in\",\"path\":[\"addTodo\",0],\"extensions\":{\"code\":\"UNAUTHENTICATED\"}}]}"), out var http);
        var mutate = http.GraphQLMutationFn("graphql", AddTodo, GraphQLTestJson.Default.AddTodoVariables, GraphQLTestJson.Default.AddTodoData);

        var ex = await Assert.ThrowsAsync<GraphQLException>(() => mutate(new AddTodoVariables("x"), CancellationToken.None));

        var error = Assert.Single(ex.Errors);
        Assert.Equal("Sign in", error.Message);
        Assert.Equal(["addTodo", "0"], error.Path);
        Assert.Equal("UNAUTHENTICATED", error.Code);
        Assert.True(ex.HasCode("unauthenticated"));
        Assert.False(ex.HasCode("BAD_USER_INPUT"));
    }

    private sealed class CapturingHandler(Func<HttpResponseMessage> responder) : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return responder();
        }
    }
}
