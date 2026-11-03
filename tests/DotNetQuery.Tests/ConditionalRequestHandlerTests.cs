using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DotNetQuery;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Tests;

public class ConditionalRequestHandlerTests
{
    private sealed record Widget(int Id, string Name);

    private sealed class Server : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, string? etag = null, DateTimeOffset? lastModified = null, bool noStore = false)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (etag is not null)
        {
            response.Headers.ETag = new EntityTagHeaderValue(etag);
        }

        if (lastModified is not null)
        {
            response.Content.Headers.LastModified = lastModified;
        }

        if (noStore)
        {
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        }

        return response;
    }

    private static (HttpClient Http, Server Server, ConditionalRequestHandler Handler) Create(ConditionalRequestOptions? options = null)
    {
        var server = new Server();
        var handler = new ConditionalRequestHandler(options ?? new ConditionalRequestOptions()) { InnerHandler = server };
        return (new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") }, server, handler);
    }

    [Fact]
    public async Task Revalidates_with_etag_and_replays_the_body_on_304()
    {
        var (http, server, handler) = Create();
        server.Respond = request => request.Headers.IfNoneMatch.Any(t => t.Tag == "\"v1\"")
            ? new HttpResponseMessage(HttpStatusCode.NotModified)
            : Json("{\"id\":1,\"name\":\"gadget\"}", etag: "\"v1\"");
        var fetcher = http.JsonFetcher<Widget>("widgets/1");

        var first = await fetcher(CancellationToken.None);
        var second = await fetcher(CancellationToken.None);

        Assert.Equal(new Widget(1, "gadget"), first);
        Assert.Equal(first, second);
        Assert.Empty(server.Requests[0].Headers.IfNoneMatch);
        Assert.Equal("\"v1\"", Assert.Single(server.Requests[1].Headers.IfNoneMatch).Tag);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task Uses_if_modified_since_without_an_etag()
    {
        var (http, server, _) = Create();
        var modified = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        server.Respond = request => request.Headers.IfModifiedSince == modified
            ? new HttpResponseMessage(HttpStatusCode.NotModified)
            : Json("[1,2,3]", lastModified: modified);

        await http.GetStringAsync("numbers");
        var replayed = await http.GetStringAsync("numbers");

        Assert.Equal("[1,2,3]", replayed);
        Assert.Equal(modified, server.Requests[1].Headers.IfModifiedSince);
    }

    [Fact]
    public async Task Changed_resources_are_remembered_anew()
    {
        var (http, server, _) = Create();
        var version = 1;
        server.Respond = request => request.Headers.IfNoneMatch.Any(t => t.Tag == $"\"v{version}\"")
            ? new HttpResponseMessage(HttpStatusCode.NotModified)
            : Json($"\"value {version}\"", etag: $"\"v{version}\"");

        Assert.Equal("\"value 1\"", await http.GetStringAsync("value"));
        version = 2;
        Assert.Equal("\"value 2\"", await http.GetStringAsync("value"));
        Assert.Equal("\"value 2\"", await http.GetStringAsync("value"));
        Assert.Equal("\"v2\"", Assert.Single(server.Requests[2].Headers.IfNoneMatch).Tag);
    }

    [Fact]
    public async Task No_store_responses_and_non_get_requests_are_not_remembered()
    {
        var (http, server, handler) = Create();
        server.Respond = _ => Json("{}", etag: "\"x\"", noStore: true);

        await http.GetStringAsync("private");
        await http.PostAsync("private", new StringContent("{}"));

        Assert.Equal(0, handler.Count);
        Assert.All(server.Requests, r => Assert.Empty(r.Headers.IfNoneMatch));
    }

    [Fact]
    public async Task Error_responses_forget_the_remembered_entry()
    {
        var (http, server, handler) = Create();
        server.Respond = _ => Json("{}", etag: "\"x\"");
        await http.GetStringAsync("gone");
        Assert.Equal(1, handler.Count);

        server.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        using var response = await http.GetAsync("gone");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task Evicts_least_recently_used_entries_and_skips_large_bodies()
    {
        var (http, server, handler) = Create(new ConditionalRequestOptions { MaxEntries = 2, MaxBodyBytes = 10 });
        server.Respond = request => request.RequestUri!.AbsolutePath.EndsWith("big", StringComparison.Ordinal)
            ? Json("\"this body is too large\"", etag: "\"big\"")
            : Json("1", etag: "\"e\"");

        await http.GetStringAsync("a");
        await http.GetStringAsync("b");
        await http.GetStringAsync("c");
        await http.GetStringAsync("big");

        Assert.Equal(2, handler.Count);
        handler.Clear();
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task Callers_with_their_own_validators_are_passed_through()
    {
        var (http, server, _) = Create();
        server.Respond = _ => Json("{}", etag: "\"x\"");
        await http.GetStringAsync("thing");
        using var request = new HttpRequestMessage(HttpMethod.Get, "thing");
        request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"mine\""));
        server.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotModified);

        using var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Equal("\"mine\"", Assert.Single(server.Requests[1].Headers.IfNoneMatch).Tag);
    }

    [Fact]
    public async Task End_to_end_refetch_of_unchanged_data_transfers_no_body()
    {
        var (http, server, _) = Create();
        var bodies = 0;
        server.Respond = request =>
        {
            if (request.Headers.IfNoneMatch.Count > 0)
            {
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }

            bodies++;
            return Json("[\"a\",\"b\"]", etag: "\"list\"");
        };
        using var client = new QueryClient(TestUtil.NoRetry(), new FakeTimeProvider());
        var query = http.JsonQuery<List<string>>(QueryKey.Create("list"), "list");

        await client.FetchAsync(query);
        var refetched = await client.FetchAsync(query, forceRefetch: true);

        Assert.Equal(["a", "b"], refetched.Data);
        Assert.Equal(1, bodies);
        Assert.Equal(2, server.Requests.Count);
    }
}
