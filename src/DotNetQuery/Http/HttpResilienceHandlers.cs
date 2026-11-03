using System.Net;
using System.Net.Http.Headers;

namespace DotNetQuery;

/// <summary>The state of a <see cref="CircuitBreakerHandler"/>.</summary>
public enum CircuitState
{
    /// <summary>Requests flow normally.</summary>
    Closed,

    /// <summary>Requests fail fast with <see cref="BrokenCircuitException"/> until the break elapses.</summary>
    Open,

    /// <summary>The break elapsed: one trial request is let through to decide whether to close or re-open.</summary>
    HalfOpen,
}

/// <summary>Options for a <see cref="CircuitBreakerHandler"/>.</summary>
public sealed record CircuitBreakerOptions
{
    /// <summary>Consecutive failures that open the circuit. Defaults to 5.</summary>
    public int FailureThreshold { get; init; } = 5;

    /// <summary>How long the circuit stays open before a trial request. Defaults to 30 seconds.</summary>
    public TimeSpan BreakDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Classifies an outcome as a failure that counts toward opening the circuit: a response (exception is
    /// <see langword="null"/>) or an exception (response is <see langword="null"/>). Defaults to transient failures
    /// (<see cref="HttpRetryPolicy.IsTransient(HttpStatusCode)"/> statuses and network errors) - a 404 or 400 means the
    /// server is healthy.
    /// </summary>
    public Func<HttpResponseMessage?, Exception?, bool>? IsFailure { get; init; }

    /// <summary>The clock. Defaults to <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider? TimeProvider { get; init; }
}

/// <summary>Thrown by <see cref="CircuitBreakerHandler"/> while the circuit is open.</summary>
/// <remarks>
/// It is an <see cref="HttpRequestException"/> without a status code (so <see cref="HttpRetryPolicy.ShouldRetry"/> treats
/// it as transient), and carries the remaining break as its retry-after hint, so
/// <see cref="HttpRetryPolicy.RespectRetryAfter"/> retries once the circuit half-opens.
/// </remarks>
public sealed class BrokenCircuitException : HttpRequestException
{
    /// <summary>Creates the exception.</summary>
    public BrokenCircuitException()
        : this("The circuit is open: the server failed repeatedly, so requests fail fast for a while.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public BrokenCircuitException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public BrokenCircuitException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A circuit breaker for an <see cref="HttpClient"/> pipeline: after <see cref="CircuitBreakerOptions.FailureThreshold"/>
/// consecutive transient failures it stops sending requests for <see cref="CircuitBreakerOptions.BreakDuration"/>
/// (failing them fast with <see cref="BrokenCircuitException"/>), then lets one trial request through. Spares a
/// struggling server the retries of every query at once.
/// </summary>
/// <example><c>services.AddHttpClient&lt;TodoApi&gt;().AddHttpMessageHandler(() =&gt; new CircuitBreakerHandler());</c></example>
public sealed class CircuitBreakerHandler : DelegatingHandler
{
    private readonly CircuitBreakerOptions _options;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private CircuitState _state = CircuitState.Closed;
    private int _failures;
    private DateTimeOffset _openUntil;
    private bool _trialInFlight;

    /// <summary>Creates a circuit breaker with default options.</summary>
    public CircuitBreakerHandler()
        : this(new CircuitBreakerOptions())
    {
    }

    /// <summary>Creates a circuit breaker with <paramref name="options"/>.</summary>
    public CircuitBreakerHandler(CircuitBreakerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = options.TimeProvider ?? TimeProvider.System;
    }

    /// <summary>Gets the current state (an open circuit whose break elapsed reports <see cref="CircuitState.HalfOpen"/>).</summary>
    public CircuitState State
    {
        get
        {
            lock (_gate)
            {
                return _state is CircuitState.Open && _time.GetUtcNow() >= _openUntil ? CircuitState.HalfOpen : _state;
            }
        }
    }

    /// <summary>Raised when the state changes.</summary>
    public event Action<CircuitState>? StateChanged;

    /// <summary>Closes the circuit and forgets recorded failures.</summary>
    public void Reset() => Transition(CircuitState.Closed, resetFailures: true);

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var isTrial = Admit();
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Abandon(isTrial);
            throw;
        }
        catch (Exception ex)
        {
            Record(IsFailure(null, ex), isTrial);
            throw;
        }

        Record(IsFailure(response, null), isTrial);
        return response;
    }

    // Throws while open; returns whether this request is the half-open trial.
    private bool Admit()
    {
        var becameHalfOpen = false;
        var isTrial = false;
        lock (_gate)
        {
            if (_state is CircuitState.Open)
            {
                var now = _time.GetUtcNow();
                if (now < _openUntil)
                {
                    var error = new BrokenCircuitException();
                    error.Data[HttpRetryPolicy.RetryAfterDataKey] = _openUntil - now;
                    throw error;
                }

                _state = CircuitState.HalfOpen;
                becameHalfOpen = true;
            }

            if (_state is CircuitState.HalfOpen)
            {
                if (_trialInFlight)
                {
                    var error = new BrokenCircuitException();
                    error.Data[HttpRetryPolicy.RetryAfterDataKey] = _options.BreakDuration;
                    throw error;
                }

                _trialInFlight = true;
                isTrial = true;
            }
        }

        if (becameHalfOpen)
        {
            RaiseStateChanged(CircuitState.HalfOpen);
        }

        return isTrial;
    }

    private void Record(bool failed, bool isTrial)
    {
        CircuitState? changed = null;
        lock (_gate)
        {
            if (isTrial)
            {
                _trialInFlight = false;
            }

            if (!failed)
            {
                _failures = 0;
                if (_state is not CircuitState.Closed)
                {
                    _state = CircuitState.Closed;
                    changed = _state;
                }
            }
            else if (_state is CircuitState.HalfOpen || ++_failures >= Math.Max(1, _options.FailureThreshold))
            {
                _state = CircuitState.Open;
                _openUntil = _time.GetUtcNow() + _options.BreakDuration;
                _failures = 0;
                changed = _state;
            }
        }

        if (changed is { } state)
        {
            RaiseStateChanged(state);
        }
    }

    private void Abandon(bool isTrial)
    {
        if (isTrial)
        {
            lock (_gate)
            {
                _trialInFlight = false;
            }
        }
    }

    private void Transition(CircuitState state, bool resetFailures)
    {
        lock (_gate)
        {
            _state = state;
            _trialInFlight = false;
            if (resetFailures)
            {
                _failures = 0;
            }
        }

        RaiseStateChanged(state);
    }

    // A classifier that throws (user code) falls back to the default classification, so the outcome is always recorded
    // and a half-open trial always releases its slot.
    private bool IsFailure(HttpResponseMessage? response, Exception? error)
    {
        if (_options.IsFailure is { } custom)
        {
            try
            {
                return custom(response, error);
            }
            catch (Exception)
            {
                // Classified below.
            }
        }

        return response is not null ? HttpRetryPolicy.IsTransient(response.StatusCode) : error is not null && HttpRetryPolicy.IsTransient(error);
    }

    private void RaiseStateChanged(CircuitState state)
    {
        try
        {
            StateChanged?.Invoke(state);
        }
        catch (Exception)
        {
            // Subscriber errors must not break requests.
        }
    }
}

/// <summary>
/// Adds a bearer token to every request and, when the server answers <c>401 Unauthorized</c>, refreshes the token once
/// (concurrent requests share one refresh) and retries the request with the new token. Queries then never retry an
/// expired-token failure with the same token, and a refresh storm cannot happen.
/// </summary>
/// <example>
/// <code>
/// services.AddHttpClient&lt;TodoApi&gt;().AddHttpMessageHandler(sp =&gt;
/// {
///     var tokens = sp.GetRequiredService&lt;TokenStore&gt;();
///     return new BearerTokenHandler(tokens.GetAccessTokenAsync, tokens.RefreshAccessTokenAsync);
/// });
/// </code>
/// </example>
public sealed class BearerTokenHandler : DelegatingHandler
{
    private readonly Func<CancellationToken, ValueTask<string?>> _getToken;
    private readonly Func<CancellationToken, ValueTask<string?>>? _refreshToken;
    private readonly object _gate = new();
    private Task<string?>? _refreshing;

    /// <summary>Creates the handler.</summary>
    /// <param name="getToken">Returns the current access token (or <see langword="null"/> to send no authorization header).</param>
    /// <param name="refreshToken">Obtains a new access token after a 401 (or <see langword="null"/> when it cannot); omit to never retry.</param>
    public BearerTokenHandler(Func<CancellationToken, ValueTask<string?>> getToken, Func<CancellationToken, ValueTask<string?>>? refreshToken = null)
    {
        _getToken = getToken ?? throw new ArgumentNullException(nameof(getToken));
        _refreshToken = refreshToken;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await _getToken(cancellationToken).ConfigureAwait(false);
        Authorize(request, token);

        // Buffer the body up front so the request can be sent a second time after a refresh.
        if (_refreshToken is not null && request.Content is not null)
        {
            await request.Content.LoadIntoBufferAsync().ConfigureAwait(false);
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is not HttpStatusCode.Unauthorized || _refreshToken is null)
        {
            return response;
        }

        var refreshed = await RefreshAsync(token, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(refreshed) || refreshed == token)
        {
            return response; // nothing better to try: surface the 401
        }

        response.Dispose();

        // Not disposed here: the response references its request message (as with any HttpClient request).
        var retry = await CloneAsync(request).ConfigureAwait(false);
        Authorize(retry, refreshed);
        return await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);
    }

    // One refresh at a time: concurrent 401s wait for the same refresh instead of each starting one.
    private Task<string?> RefreshAsync(string? failedToken, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_refreshing is { IsCompleted: false } inFlight)
            {
                return inFlight.WaitAsync(cancellationToken);
            }

            _refreshing = Refresh();
            return _refreshing.WaitAsync(cancellationToken);
        }

        async Task<string?> Refresh()
        {
            // Another request may already have refreshed since this one failed.
            var current = await _getToken(CancellationToken.None).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(current) && current != failedToken)
            {
                return current;
            }

            return await _refreshToken!(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static void Authorize(HttpRequestMessage request, string? token) =>
        request.Headers.Authorization = string.IsNullOrEmpty(token) ? null : new AuthenticationHeaderValue("Bearer", token);

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version, VersionPolicy = request.VersionPolicy };
        foreach (var (name, values) in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(name, values);
        }

        foreach (var option in request.Options)
        {
            ((IDictionary<string, object?>)clone.Options)[option.Key] = option.Value;
        }

        if (request.Content is not null)
        {
            var body = await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            clone.Content = new ByteArrayContent(body);
            foreach (var (name, values) in request.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(name, values);
            }
        }

        return clone;
    }
}
