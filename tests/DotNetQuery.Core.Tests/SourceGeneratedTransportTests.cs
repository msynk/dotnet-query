using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DotNetQuery;
using DotNetQuery.GraphQL;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Core.Tests;

public sealed record GadgetDto(int Id, string Name);

public sealed record ViewerDto(GadgetDto Gadget);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(GadgetDto))]
[JsonSerializable(typeof(ViewerDto))]
internal sealed partial class TransportTestJsonContext : JsonSerializerContext;

/// <summary>The trim/AOT-safe (JsonTypeInfo) transport overloads.</summary>
public class SourceGeneratedTransportTests
{
    private static HttpClient Http(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new StubHandler(responder)) { BaseAddress = new Uri("https://api.test/") };

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task JsonQuery_definition_fetches_with_source_generated_metadata()
    {
        using var client = new QueryClient(new QueryClientOptions { DefaultRetryCount = 0 }, new FakeTimeProvider());
        var http = Http(_ => Json("{\"id\":3,\"name\":\"widget\"}"));
        var query = http.JsonQuery(QueryKey.Create("gadget", 3), "gadgets/3", TransportTestJsonContext.Default.GadgetDto);

        var result = await client.FetchAsync(query);

        Assert.Equal(new GadgetDto(3, "widget"), result.Data);
    }

    [Fact]
    public async Task GraphQL_typeinfo_overload_sends_variables_and_unwraps_data()
    {
        string? body = null;
        var http = Http(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("{\"data\":{\"gadget\":{\"id\":9,\"name\":\"probe\"}}}");
        });

        var fetcher = http.GraphQLFetcher(
            "graphql",
            "query($id:Int!){ gadget(id:$id){ id name } }",
            TransportTestJsonContext.Default.ViewerDto,
            new JsonObject { ["id"] = 9 });

        var viewer = await fetcher(CancellationToken.None);

        Assert.Equal(new GadgetDto(9, "probe"), viewer.Gadget);
        using var sent = JsonDocument.Parse(body!);
        Assert.Equal(9, sent.RootElement.GetProperty("variables").GetProperty("id").GetInt32());
        Assert.StartsWith("query(", sent.RootElement.GetProperty("query").GetString());
    }

    [Fact]
    public async Task GraphQL_typeinfo_overload_surfaces_errors()
    {
        var http = Http(_ => Json("{\"errors\":[{\"message\":\"not authorized\"}],\"data\":null}"));
        var fetcher = http.GraphQLFetcher("graphql", "{ gadget { id } }", TransportTestJsonContext.Default.ViewerDto);

        var error = await Assert.ThrowsAsync<GraphQLException>(() => fetcher(CancellationToken.None));

        Assert.Equal("not authorized", Assert.Single(error.Errors).Message);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
