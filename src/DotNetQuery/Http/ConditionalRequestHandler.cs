using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;

namespace DotNetQuery;

/// <summary>Options for <see cref="ConditionalRequestHandler"/>.</summary>
public sealed record ConditionalRequestOptions
{
    /// <summary>The maximum number of responses remembered (least recently used are evicted). Defaults to 256.</summary>
    public int MaxEntries { get; init; } = 256;

    /// <summary>Responses with a larger body are not remembered. Defaults to 4 MiB.</summary>
    public long MaxBodyBytes { get; init; } = 4 * 1024 * 1024;
}

/// <summary>
/// An <see cref="HttpClient"/> handler that makes repeated GETs conditional: it remembers each successful response
/// that carries an <c>ETag</c> or <c>Last-Modified</c> validator, sends <c>If-None-Match</c> /
/// <c>If-Modified-Since</c> on the next request for the same URI, and turns a <c>304 Not Modified</c> back into the
/// remembered <c>200</c> response. Refetches of unchanged resources then cost a round trip but no body transfer,
/// and every fetcher (including the HTTP/JSON helpers) works unchanged.
/// </summary>
/// <remarks>
/// <para>
/// Add it to a client pipeline: <c>services.AddHttpClient&lt;TodoApi&gt;().AddHttpMessageHandler(() =&gt; new ConditionalRequestHandler())</c>,
/// or <c>new HttpClient(new ConditionalRequestHandler { InnerHandler = new HttpClientHandler() })</c>. Browsers
/// already do this for Blazor WebAssembly through their HTTP cache, so it is not needed there.
/// </para>
/// <para>
/// Only GET requests without their own conditional headers participate. Responses marked
/// <c>Cache-Control: no-store</c>, larger than <see cref="ConditionalRequestOptions.MaxBodyBytes"/>, or without a
/// validator are not remembered. Remembered responses are keyed by absolute URI only - do not share one handler
/// between users if responses for the same URI differ per user (for example by an <c>Authorization</c> header);
/// give each user (or each <see cref="HttpClient"/> scope) its own handler.
/// </para>
/// </remarks>
public sealed class ConditionalRequestHandler : DelegatingHandler
{
    private readonly ConcurrentDictionary<string, Remembered> _responses = new(StringComparer.Ordinal);
    private readonly ConditionalRequestOptions _options;
    private long _clock;

    /// <summary>Creates a handler with default options.</summary>
    public ConditionalRequestHandler()
        : this(new ConditionalRequestOptions())
    {
    }

    /// <summary>Creates a handler with <paramref name="options"/>.</summary>
    public ConditionalRequestHandler(ConditionalRequestOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>Gets the number of remembered responses.</summary>
    public int Count => _responses.Count;

    /// <summary>Forgets every remembered response (for example when the signed-in user changes).</summary>
    public void Clear() => _responses.Clear();

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get || request.RequestUri is not { IsAbsoluteUri: true } uri
            || request.Headers.IfNoneMatch.Count > 0 || request.Headers.IfModifiedSince is not null)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var cacheKey = uri.AbsoluteUri;
        HttpResponseMessage response;
        if (_responses.TryGetValue(cacheKey, out var remembered))
        {
            if (remembered.ETag is { } etag)
            {
                request.Headers.IfNoneMatch.Add(etag);
            }
            else if (remembered.LastModified is { } lastModified)
            {
                request.Headers.IfModifiedSince = lastModified;
            }

            try
            {
                response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // The validator is this handler's, not the caller's: take it off again, so an outer handler that sends
                // the request again (a retry, or a copy after a token refresh) does not look like a conditional request
                // of its own, which would bypass this handler and surface a raw 304.
                request.Headers.IfNoneMatch.Clear();
                request.Headers.IfModifiedSince = null;
            }
        }
        else
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (response.StatusCode is HttpStatusCode.NotModified && remembered is not null)
        {
            remembered.Touch(Interlocked.Increment(ref _clock));
            var replay = remembered.ToResponse(request);
            response.Dispose();
            return replay;
        }

        if (response.StatusCode is HttpStatusCode.OK)
        {
            await RememberAsync(cacheKey, response, cancellationToken).ConfigureAwait(false);
        }
        else if (response.StatusCode is not HttpStatusCode.NotModified)
        {
            _responses.TryRemove(cacheKey, out _);
        }

        return response;
    }

    private async Task RememberAsync(string cacheKey, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var etag = response.Headers.ETag;
        var lastModified = response.Content.Headers.LastModified;
        if ((etag is null && lastModified is null) || response.Headers.CacheControl?.NoStore == true)
        {
            _responses.TryRemove(cacheKey, out _);
            return;
        }

        if (response.Content.Headers.ContentLength is { } length && length > _options.MaxBodyBytes)
        {
            _responses.TryRemove(cacheKey, out _);
            return;
        }

        // Buffer the body so it can be replayed; the caller reads the same bytes from a fresh content.
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var contentHeaders = response.Content.Headers.ToArray();
        response.Content = Remembered.CreateContent(body, contentHeaders);
        if (body.LongLength > _options.MaxBodyBytes)
        {
            _responses.TryRemove(cacheKey, out _);
            return;
        }

        _responses[cacheKey] = new Remembered(etag, lastModified, body, contentHeaders, response.Version, Interlocked.Increment(ref _clock));
        Evict();
    }

    private void Evict()
    {
        var excess = _responses.Count - Math.Max(1, _options.MaxEntries);
        if (excess <= 0)
        {
            return;
        }

        foreach (var (key, _) in _responses.OrderBy(pair => pair.Value.LastUsed).Take(excess).ToArray())
        {
            _responses.TryRemove(key, out _);
        }
    }

    private sealed class Remembered(
        EntityTagHeaderValue? etag,
        DateTimeOffset? lastModified,
        byte[] body,
        KeyValuePair<string, IEnumerable<string>>[] contentHeaders,
        Version version,
        long lastUsed)
    {
        private long _lastUsed = lastUsed;

        public EntityTagHeaderValue? ETag { get; } = etag;

        public DateTimeOffset? LastModified { get; } = lastModified;

        public long LastUsed => Volatile.Read(ref _lastUsed);

        public void Touch(long now) => Volatile.Write(ref _lastUsed, now);

        public HttpResponseMessage ToResponse(HttpRequestMessage request)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Version = version,
                Content = CreateContent(body, contentHeaders),
            };

            if (ETag is not null)
            {
                response.Headers.ETag = ETag;
            }

            return response;
        }

        public static ByteArrayContent CreateContent(byte[] body, KeyValuePair<string, IEnumerable<string>>[] headers)
        {
            var content = new ByteArrayContent(body);
            foreach (var (name, values) in headers)
            {
                content.Headers.TryAddWithoutValidation(name, values);
            }

            return content;
        }
    }
}
