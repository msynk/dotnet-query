using System.Net;
using System.Net.Sockets;

namespace DotNetQuery;

/// <summary>
/// HTTP-aware retry classification and back-off. Plug these into <see cref="QueryOptions.ShouldRetry"/> /
/// <see cref="QueryOptions.RetryDelay"/> (or the client defaults, or a mutation's options) so transient
/// failures are retried while client errors such as 401, 403 or 404 fail immediately.
/// </summary>
/// <example>
/// <code>
/// builder.Services.AddDotNetQuery(options =>
/// {
///     options.DefaultShouldRetry = HttpRetryPolicy.ShouldRetry;
///     options.DefaultRetryDelay = HttpRetryPolicy.RespectRetryAfter();
/// });
/// </code>
/// </example>
public static class HttpRetryPolicy
{
    /// <summary>
    /// The <see cref="Exception.Data"/> key under which the HTTP fetchers in this package store a response's
    /// <c>Retry-After</c> delay (a <see cref="TimeSpan"/>). Fetchers of your own can set it too, so that
    /// <see cref="RespectRetryAfter"/> honors it.
    /// </summary>
    public const string RetryAfterDataKey = "DotNetQuery.RetryAfter";

    /// <summary>A <see cref="RetryPredicate"/> that retries only <see cref="IsTransient(Exception)">transient</see> failures.</summary>
    public static RetryPredicate ShouldRetry { get; } = static (_, error) => IsTransient(error);

    /// <summary>
    /// Returns whether <paramref name="error"/> is worth retrying: connection failures and timeouts, and the
    /// HTTP statuses 408 (Request Timeout), 429 (Too Many Requests) and 5xx other than 501 (Not Implemented)
    /// and 505 (HTTP Version Not Supported). Other 4xx statuses (authentication, authorization, not found,
    /// validation) are permanent, as are deserialization errors and anything else unrecognized.
    /// </summary>
    public static bool IsTransient(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        switch (error)
        {
            case HttpRequestException http:
                // No status code means the request never got a response (DNS, connection refused/reset, TLS).
                return http.StatusCode is not { } status || IsTransient(status);
            case TimeoutException:
            case IOException:
            case SocketException:
                return true;
            case TaskCanceledException cancelled:
                // HttpClient.Timeout surfaces as a TaskCanceledException wrapping a TimeoutException. A
                // cancellation requested by the caller never reaches the retry loop.
                return cancelled.InnerException is TimeoutException;
            default:
                return false;
        }
    }

    /// <summary>Returns whether a response with <paramref name="status"/> is worth retrying (see <see cref="IsTransient(Exception)"/>).</summary>
    public static bool IsTransient(HttpStatusCode status) => status switch
    {
        HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests => true,
        HttpStatusCode.NotImplemented or HttpStatusCode.HttpVersionNotSupported => false,
        _ => (int)status >= 500 && (int)status <= 599,
    };

    /// <summary>
    /// Returns whether <paramref name="error"/> is an HTTP conflict - <c>409 Conflict</c> or <c>412 Precondition Failed</c>
    /// (an optimistic-concurrency check such as <c>If-Match</c> failed) - which retrying cannot fix: the client must
    /// reconcile with the server's current state first.
    /// </summary>
    public static bool IsConflict(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error is HttpRequestException { StatusCode: HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed };
    }

    /// <summary>
    /// Gets the <c>Retry-After</c> delay carried by <paramref name="error"/> (see <see cref="RetryAfterDataKey"/>),
    /// or <see langword="null"/> when there is none.
    /// </summary>
    public static TimeSpan? GetRetryAfter(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Data[RetryAfterDataKey] is TimeSpan delay ? delay : null;
    }

    /// <summary>
    /// A <see cref="RetryDelayStrategy"/> that waits for the server-specified <c>Retry-After</c> delay when the
    /// failure carries one (capped at <paramref name="maxDelay"/>, default one minute), and otherwise defers to
    /// <paramref name="fallback"/> (default <see cref="RetryPolicy.Default"/>).
    /// </summary>
    public static RetryDelayStrategy RespectRetryAfter(RetryDelayStrategy? fallback = null, TimeSpan? maxDelay = null)
    {
        var next = fallback ?? RetryPolicy.Default;
        var cap = maxDelay is { } max && max >= TimeSpan.Zero ? max : TimeSpan.FromMinutes(1);
        return (attempt, error) => GetRetryAfter(error) is { } delay
            ? (delay < TimeSpan.Zero ? TimeSpan.Zero : delay > cap ? cap : delay)
            : next(attempt, error);
    }

    /// <summary>
    /// Throws an <see cref="HttpRequestException"/> (with <see cref="HttpRequestException.StatusCode"/> set and any
    /// <c>Retry-After</c> recorded under <see cref="RetryAfterDataKey"/>) when <paramref name="response"/> is not
    /// successful. The message matches <see cref="HttpResponseMessage.EnsureSuccessStatusCode"/>.
    /// </summary>
    internal static void EnsureSuccess(HttpResponseMessage response, TimeProvider? timeProvider = null)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var error = new HttpRequestException(
            $"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}).",
            inner: null,
            response.StatusCode);

        if (response.Headers.RetryAfter is { } retryAfter)
        {
            var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
            var delay = retryAfter.Delta ?? (retryAfter.Date is { } date ? date - now : null);
            if (delay is { } d)
            {
                error.Data[RetryAfterDataKey] = d < TimeSpan.Zero ? TimeSpan.Zero : d;
            }
        }

        throw error;
    }
}
