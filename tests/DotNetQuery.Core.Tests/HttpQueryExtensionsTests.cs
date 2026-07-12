using System.Net;
using System.Text;
using DotNetQuery;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Core.Tests;

public class HttpQueryExtensionsTests
{
    private sealed record Widget(int Id, string Name);

    private static QueryClient CreateClient() => new(new QueryClientOptions(), new FakeTimeProvider());

    private static HttpClient Http(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new StubHandler(responder)) { BaseAddress = new Uri("https://api.test/") };

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task JsonFetcher_deserializes_the_response()
    {
        var http = Http(_ => Json("{\"id\":7,\"name\":\"gadget\"}"));
        var fetcher = http.JsonFetcher<Widget>("widgets/7");

        var widget = await fetcher(CancellationToken.None);

        Assert.Equal(new Widget(7, "gadget"), widget);
    }

    [Fact]
    public async Task JsonFetcher_throws_when_body_is_null()
    {
        var http = Http(_ => Json("null"));
        var fetcher = http.JsonFetcher<Widget>("widgets/missing");

        await Assert.ThrowsAsync<InvalidOperationException>(() => fetcher(CancellationToken.None));
    }

    [Fact]
    public async Task JsonFetcher_throws_on_non_success_status()
    {
        var http = Http(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var fetcher = http.JsonFetcher<Widget>("widgets/boom");

        await Assert.ThrowsAsync<HttpRequestException>(() => fetcher(CancellationToken.None));
    }

    [Fact]
    public async Task FetchJsonAsync_populates_the_cache_under_the_key()
    {
        using var client = CreateClient();
        var requested = 0;
        var http = Http(req =>
        {
            requested++;
            Assert.Equal("https://api.test/widgets/7", req.RequestUri!.ToString());
            return Json("{\"id\":7,\"name\":\"gadget\"}");
        });
        var key = QueryKey.Create<Widget>(7);

        var result = await client.FetchJsonAsync<Widget>(key, http, "widgets/7");

        Assert.True(result.IsSuccess);
        Assert.Equal(new Widget(7, "gadget"), result.Data);
        Assert.Equal(new Widget(7, "gadget"), client.GetQueryData<Widget>(key).Data);
        Assert.Equal(1, requested);
    }

    [Fact]
    public async Task PrefetchJsonAsync_swallows_errors()
    {
        using var client = CreateClient();
        var http = Http(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        var key = QueryKey.Create<Widget>("pref");

        // Best-effort: must not throw even though the endpoint fails.
        await client.PrefetchJsonAsync<Widget>(key, http, "widgets/x", new QueryOptions { RetryCount = 0 });

        Assert.False(client.GetQueryData<Widget>(key).IsSuccess);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
