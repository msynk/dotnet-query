using System.Net;
using System.Text;
using DotNetQuery;
using DotNetQuery.GraphQL;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Core.Tests;

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
