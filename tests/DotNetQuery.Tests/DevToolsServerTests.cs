using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DotNetQuery;
using DotNetQuery.DevTools;
using Xunit;

namespace DotNetQuery.Tests;

public class DevToolsServerTests
{
    [Fact]
    public async Task The_server_serves_the_live_export_on_loopback()
    {
        using var client = new QueryClient(TestUtil.NoRetry());
        using var devtools = new QueryDevTools(client);
        await using var server = QueryDevToolsServer.Start(devtools);
        using var http = new HttpClient();

        Assert.Equal("127.0.0.1", server.Address.Host);
        await client.FetchAsync(QueryKey.Create("todos", 1), _ => Task.FromResult("milk"));

        using var document = JsonDocument.Parse(await http.GetStringAsync(server.Address));
        Assert.Equal(1, document.RootElement.GetProperty("stats").GetProperty("totalQueries").GetInt32());
        Assert.Contains("todos", document.RootElement.GetProperty("queries")[0].GetProperty("key").GetString());
        Assert.DoesNotContain("milk", document.RootElement.ToString());   // cached data is never exported

        await client.FetchAsync(QueryKey.Create("todos", 2), _ => Task.FromResult("bread"));
        using var again = JsonDocument.Parse(await http.GetStringAsync(new Uri(server.Address, "export")));
        Assert.Equal(2, again.RootElement.GetProperty("stats").GetProperty("totalQueries").GetInt32());

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(new Uri(server.Address, "other"))).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await http.PostAsync(server.Address, new StringContent(""))).StatusCode);
    }

    [Fact]
    public async Task An_access_token_is_required_when_configured()
    {
        using var client = new QueryClient(TestUtil.NoRetry());
        using var devtools = new QueryDevTools(client);
        await using var server = QueryDevToolsServer.Start(devtools, new QueryDevToolsServerOptions { AccessToken = "s3cret" });
        using var http = new HttpClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(server.Address)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(new Uri(server.Address, "?token=wrong"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(new Uri(server.Address, "?token=s3cret"))).StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, server.Address);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "s3cret");
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Disposing_stops_listening()
    {
        using var client = new QueryClient(TestUtil.NoRetry());
        using var devtools = new QueryDevTools(client);
        var server = QueryDevToolsServer.Start(devtools);
        var address = server.Address;
        await server.DisposeAsync();
        await server.DisposeAsync();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        await Assert.ThrowsAnyAsync<Exception>(() => http.GetStringAsync(address));
    }
}
