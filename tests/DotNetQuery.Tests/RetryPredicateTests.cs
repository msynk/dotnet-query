using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DotNetQuery;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Tests;

public class RetryPredicateTests
{
    private static QueryClient CreateClient(Action<QueryClientOptions>? configure = null)
    {
        var options = new QueryClientOptions
        {
            DefaultRetryCount = 3,
            DefaultRetryDelay = RetryPolicy.Constant(TimeSpan.Zero),
            GarbageCollectionInterval = Timeout.InfiniteTimeSpan,
        };
        configure?.Invoke(options);
        return new QueryClient(options, new FakeTimeProvider());
    }

    [Fact]
    public async Task Query_is_not_retried_when_the_predicate_vetoes()
    {
        using var client = CreateClient();
        var calls = 0;

        var result = await client.FetchAsync<int>(
            QueryKey.Create("veto"),
            _ => { calls++; throw new InvalidOperationException("permanent"); },
            new QueryOptions { ShouldRetry = (_, _) => false });

        Assert.Equal(1, calls);
        Assert.True(result.IsError);
        Assert.Equal(1, result.FailureCount);
        Assert.Equal("permanent", result.Error!.Message);
    }

    [Fact]
    public async Task Predicate_receives_the_running_failure_count_and_can_stop_mid_way()
    {
        using var client = CreateClient();
        var seen = new List<int>();
        var calls = 0;

        var result = await client.FetchAsync<int>(
            QueryKey.Create("stop-at-2"),
            _ => { calls++; throw new InvalidOperationException("boom"); },
            new QueryOptions { ShouldRetry = (count, _) => { seen.Add(count); return count < 2; } });

        Assert.Equal(2, calls);
        Assert.Equal([1, 2], seen);
        Assert.Equal(2, result.FailureCount);
    }

    [Fact]
    public async Task Predicate_never_extends_the_retry_budget()
    {
        using var client = CreateClient(o => o.DefaultRetryCount = 1);
        var calls = 0;

        await client.FetchAsync<int>(
            QueryKey.Create("budget"),
            _ => { calls++; throw new InvalidOperationException("boom"); },
            new QueryOptions { ShouldRetry = (_, _) => true });

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Throwing_predicate_counts_as_no_retry_and_keeps_the_fetch_error()
    {
        using var client = CreateClient();
        var calls = 0;

        var result = await client.FetchAsync<int>(
            QueryKey.Create("throwing-predicate"),
            _ => { calls++; throw new InvalidOperationException("fetch error"); },
            new QueryOptions { ShouldRetry = (_, _) => throw new FormatException("predicate bug") });

        Assert.Equal(1, calls);
        Assert.IsType<InvalidOperationException>(result.Error);
    }

    [Fact]
    public async Task Client_default_applies_and_per_query_option_overrides_it()
    {
        using var client = CreateClient(o => o.DefaultShouldRetry = (_, _) => false);
        var defaultCalls = 0;
        var overrideCalls = 0;

        await client.FetchAsync<int>(QueryKey.Create("default"), _ => { defaultCalls++; throw new InvalidOperationException(); });
        await client.FetchAsync<int>(
            QueryKey.Create("override"),
            _ => { overrideCalls++; throw new InvalidOperationException(); },
            new QueryOptions { ShouldRetry = (_, _) => true });

        Assert.Equal(1, defaultCalls);
        Assert.Equal(4, overrideCalls);
    }

    [Fact]
    public async Task Mutation_is_not_retried_when_the_predicate_vetoes()
    {
        using var client = CreateClient();
        var calls = 0;
        var mutation = client.CreateMutation<int, int>(
            (_, _) => { calls++; throw new InvalidOperationException("rejected"); },
            new MutationOptions<int, int> { RetryCount = 3, ThrowOnError = false, ShouldRetry = (_, error) => error is not InvalidOperationException });

        var result = await mutation.MutateAsync(1);

        Assert.Equal(1, calls);
        Assert.True(result.IsError);
        Assert.Equal(1, result.State.FailureCount);
    }

    [Fact]
    public async Task Mutation_retries_while_the_predicate_allows()
    {
        using var client = CreateClient();
        var calls = 0;
        var mutation = client.CreateMutation<int, int>(
            (v, _) => ++calls < 3 ? throw new TimeoutException() : Task.FromResult(v * 2),
            new MutationOptions<int, int> { RetryCount = 5, ShouldRetry = HttpRetryPolicy.ShouldRetry });

        var result = await mutation.MutateAsync(21);

        Assert.Equal(3, calls);
        Assert.Equal(42, result.Data);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.Conflict, false)]
    [InlineData(HttpStatusCode.NotImplemented, false)]
    [InlineData(HttpStatusCode.HttpVersionNotSupported, false)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.GatewayTimeout, true)]
    public void Http_status_classification(HttpStatusCode status, bool transient)
    {
        Assert.Equal(transient, HttpRetryPolicy.IsTransient(status));
        Assert.Equal(transient, HttpRetryPolicy.IsTransient(new HttpRequestException("x", null, status)));
    }

    [Fact]
    public void Exception_classification()
    {
        Assert.True(HttpRetryPolicy.IsTransient(new HttpRequestException("connection refused")));
        Assert.True(HttpRetryPolicy.IsTransient(new TimeoutException()));
        Assert.True(HttpRetryPolicy.IsTransient(new IOException()));
        Assert.True(HttpRetryPolicy.IsTransient(new TaskCanceledException("timeout", new TimeoutException())));
        Assert.False(HttpRetryPolicy.IsTransient(new TaskCanceledException()));
        Assert.False(HttpRetryPolicy.IsTransient(new JsonException()));
        Assert.False(HttpRetryPolicy.IsTransient(new InvalidOperationException()));
    }

    [Fact]
    public async Task Json_fetcher_records_status_and_retry_after_on_failure()
    {
        var http = Http(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return response;
        });

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => http.JsonFetcher<int[]>("busy")(CancellationToken.None));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(7), HttpRetryPolicy.GetRetryAfter(error));
        Assert.Contains("503", error.Message);
    }

    [Fact]
    public void Respect_retry_after_prefers_the_server_delay_caps_it_and_falls_back()
    {
        var strategy = HttpRetryPolicy.RespectRetryAfter(RetryPolicy.Constant(TimeSpan.FromSeconds(1)), TimeSpan.FromSeconds(30));
        var withHeader = new HttpRequestException("x", null, HttpStatusCode.TooManyRequests);
        withHeader.Data[HttpRetryPolicy.RetryAfterDataKey] = TimeSpan.FromSeconds(5);
        var tooLong = new HttpRequestException("x", null, HttpStatusCode.TooManyRequests);
        tooLong.Data[HttpRetryPolicy.RetryAfterDataKey] = TimeSpan.FromHours(1);

        Assert.Equal(TimeSpan.FromSeconds(5), strategy(0, withHeader));
        Assert.Equal(TimeSpan.FromSeconds(30), strategy(0, tooLong));
        Assert.Equal(TimeSpan.FromSeconds(1), strategy(0, new HttpRequestException("x")));
    }

    [Fact]
    public async Task Http_policy_end_to_end_retries_server_errors_but_not_not_found()
    {
        using var client = CreateClient(o => o.DefaultShouldRetry = HttpRetryPolicy.ShouldRetry);
        var notFoundRequests = 0;
        var unavailableRequests = 0;
        var http = Http(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("missing", StringComparison.Ordinal))
            {
                notFoundRequests++;
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            unavailableRequests++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });

        var missing = await client.FetchAsync(http.JsonQuery<int[]>(QueryKey.Create("missing"), "missing"));
        var unavailable = await client.FetchAsync(http.JsonQuery<int[]>(QueryKey.Create("unavailable"), "unavailable"));

        Assert.Equal(1, notFoundRequests);
        Assert.Equal(4, unavailableRequests);
        Assert.True(missing.IsError);
        Assert.True(unavailable.IsError);
    }

    private static HttpClient Http(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new Responder(responder)) { BaseAddress = new Uri("https://api.test/") };

    private sealed class Responder(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
