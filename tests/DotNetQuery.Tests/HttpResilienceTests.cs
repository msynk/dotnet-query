using System.Net;
using System.Net.Http.Headers;
using DotNetQuery;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Tests;

public class HttpResilienceTests
{
    private sealed class Server(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }

            return Task.FromResult(respond(request));
        }
    }

    [Fact]
    public async Task Circuit_opens_after_consecutive_failures_fails_fast_then_half_opens()
    {
        var time = new FakeTimeProvider();
        var healthy = false;
        var server = new Server(_ => new HttpResponseMessage(healthy ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable));
        var breaker = new CircuitBreakerHandler(new CircuitBreakerOptions { FailureThreshold = 3, BreakDuration = TimeSpan.FromSeconds(10), TimeProvider = time })
        {
            InnerHandler = server,
        };
        using var http = new HttpClient(breaker) { BaseAddress = new Uri("https://api.test/") };
        var states = new List<CircuitState>();
        breaker.StateChanged += states.Add;

        for (var i = 0; i < 3; i++)
        {
            (await http.GetAsync("x")).Dispose();
        }

        Assert.Equal(CircuitState.Open, breaker.State);
        var broken = await Assert.ThrowsAsync<BrokenCircuitException>(() => http.GetAsync("x"));
        Assert.Equal(TimeSpan.FromSeconds(10), HttpRetryPolicy.GetRetryAfter(broken));
        Assert.True(HttpRetryPolicy.IsTransient(broken));
        Assert.Equal(3, server.Requests.Count); // failed fast, nothing sent

        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(CircuitState.HalfOpen, breaker.State);
        healthy = true;
        using var trial = await http.GetAsync("x");

        Assert.Equal(HttpStatusCode.OK, trial.StatusCode);
        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.Equal([CircuitState.Open, CircuitState.HalfOpen, CircuitState.Closed], states);
    }

    [Fact]
    public async Task Client_errors_do_not_open_the_circuit_and_a_failed_trial_reopens_it()
    {
        var time = new FakeTimeProvider();
        var status = HttpStatusCode.NotFound;
        var breaker = new CircuitBreakerHandler(new CircuitBreakerOptions { FailureThreshold = 2, TimeProvider = time })
        {
            InnerHandler = new Server(_ => new HttpResponseMessage(status)),
        };
        using var http = new HttpClient(breaker) { BaseAddress = new Uri("https://api.test/") };

        for (var i = 0; i < 5; i++)
        {
            (await http.GetAsync("missing")).Dispose();
        }

        Assert.Equal(CircuitState.Closed, breaker.State);

        status = HttpStatusCode.BadGateway;
        (await http.GetAsync("x")).Dispose();
        (await http.GetAsync("x")).Dispose();
        Assert.Equal(CircuitState.Open, breaker.State);

        time.Advance(TimeSpan.FromSeconds(30));
        (await http.GetAsync("x")).Dispose(); // trial fails
        Assert.Equal(CircuitState.Open, breaker.State);
    }

    [Fact]
    public async Task Bearer_token_is_refreshed_once_on_401_and_the_request_retried()
    {
        var token = "expired";
        var refreshes = 0;
        var server = new Server(request =>
            request.Headers.Authorization?.Parameter == "fresh"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") }
                : new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var handler = new BearerTokenHandler(
            _ => ValueTask.FromResult<string?>(token),
            async _ => { Interlocked.Increment(ref refreshes); await Task.Delay(20); token = "fresh"; return token; })
        {
            InnerHandler = server,
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") };

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => http.PostAsync("orders", new StringContent("{\"qty\":1}"))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(1, refreshes); // concurrent 401s share one refresh
        lock (server.Requests)
        {
            var retried = server.Requests.Where(r => r.Headers.Authorization?.Parameter == "fresh").ToList();
            Assert.Equal(5, retried.Count);
            Assert.All(retried, r => Assert.Equal("{\"qty\":1}", r.Content!.ReadAsStringAsync().Result)); // body replayed
        }
    }

    [Fact]
    public async Task Failed_refresh_surfaces_the_401()
    {
        var handler = new BearerTokenHandler(_ => ValueTask.FromResult<string?>("t"), _ => ValueTask.FromResult<string?>(null))
        {
            InnerHandler = new Server(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)),
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") };

        using var response = await http.GetAsync("me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Batch_loader_coalesces_many_queries_into_one_request()
    {
        var batches = new List<IReadOnlyList<int>>();
        var loader = new BatchLoader<int, string>((ids, _) =>
        {
            lock (batches)
            {
                batches.Add(ids);
            }

            return Task.FromResult<IReadOnlyDictionary<int, string>>(ids.Where(id => id != 404).ToDictionary(id => id, id => $"user {id}"));
        }, new BatchLoaderOptions { Delay = TimeSpan.FromMilliseconds(200) }); // fetchers start on separate thread-pool threads: a window, not one tick
        using var client = new QueryClient(TestUtil.NoRetry());

        var results = await Task.WhenAll(new[] { 1, 2, 3, 2, 404 }.Select(id =>
            client.FetchAsync(QueryKey.Create("user", id), ct => loader.LoadAsync(id, ct))));

        Assert.Single(batches);
        Assert.Equal([1, 2, 3, 404], batches[0].Order());
        Assert.Equal("user 3", results[2].Data);
        Assert.IsType<KeyNotFoundException>(results[4].Error);
    }

    [Fact]
    public async Task Batch_loader_splits_large_bursts_and_shares_failures()
    {
        var calls = 0;
        var loader = new BatchLoader<int, int>(
            (ids, _) => Interlocked.Increment(ref calls) == 1 ? throw new HttpRequestException("down") : Task.FromResult<IReadOnlyDictionary<int, int>>(ids.ToDictionary(i => i)),
            new BatchLoaderOptions { MaxBatchSize = 2, Delay = TimeSpan.FromMilliseconds(10) });

        var loads = Enumerable.Range(0, 4).Select(i => loader.LoadAsync(i)).ToArray();
        await Assert.ThrowsAsync<HttpRequestException>(() => loads[0]);
        await Assert.ThrowsAsync<HttpRequestException>(() => loads[1]);

        Assert.Equal(3, await loads[3]);
        Assert.Equal(2, calls);
    }
}
