using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace DotNetQuery.DevTools;

/// <summary>Options for <see cref="QueryDevToolsServer"/>.</summary>
public sealed record QueryDevToolsServerOptions
{
    /// <summary>The loopback port to listen on. 0 (the default) picks a free port; read it from <see cref="QueryDevToolsServer.Address"/>.</summary>
    public int Port { get; init; }

    /// <summary>
    /// When set, every request must carry it - as <c>?token=...</c> or <c>Authorization: Bearer ...</c> - so other
    /// local processes cannot read the cache's keys and errors. <see langword="null"/> (the default) accepts any local request.
    /// </summary>
    public string? AccessToken { get; init; }
}

/// <summary>
/// Serves a <see cref="QueryDevTools"/> export (<see cref="QueryDevTools.ExportJson"/>) over HTTP on the loopback
/// interface, so the <c>DotNetQuery.DevTools.Cli</c> dashboard (<c>watch http://127.0.0.1:PORT/</c>) can inspect a
/// running desktop, mobile or server app live - the counterpart of the Blazor DevTools panel for apps without one.
/// </summary>
/// <remarks>
/// Development only: the export carries query keys, statuses, timings and error messages (never cached data or mutation
/// variables). It listens on 127.0.0.1 only; set <see cref="QueryDevToolsServerOptions.AccessToken"/> on shared
/// machines. <c>GET /</c> (or <c>/export</c>) returns the export; anything else is 404.
/// </remarks>
[UnsupportedOSPlatform("browser")]
public sealed class QueryDevToolsServer : IAsyncDisposable, IDisposable
{
    private const string ReflectionMessage = "The export serializes diagnostic records with reflection-based JSON; use it for development only.";
    private const int MaxRequestBytes = 16 * 1024;

    private readonly QueryDevTools _devTools;
    private readonly string? _token;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;
    private int _disposed;

    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    private QueryDevToolsServer(QueryDevTools devTools, QueryDevToolsServerOptions options)
    {
        _devTools = devTools;
        _token = string.IsNullOrEmpty(options.AccessToken) ? null : options.AccessToken;
        _listener = new TcpListener(IPAddress.Loopback, options.Port);
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Address = new Uri($"http://127.0.0.1:{port}/");
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>The address to point the CLI at (for example <c>http://127.0.0.1:9230/</c>).</summary>
    public Uri Address { get; }

    /// <summary>Starts serving <paramref name="devTools"/>'s export on the loopback interface.</summary>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    public static QueryDevToolsServer Start(QueryDevTools devTools, QueryDevToolsServerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(devTools);
        return new QueryDevToolsServer(devTools, options ?? new QueryDevToolsServerOptions());
    }

    /// <summary>Stops listening.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stop.Cancel();
        _listener.Stop();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Stopping.
        }

        _stop.Dispose();
    }

    /// <summary>Stops listening.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The constructor (the only entry point) is annotated.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "The constructor (the only entry point) is annotated.")]
    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient connection;
            try
            {
                connection = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            _ = Task.Run(() => ServeAsync(connection));
        }
    }

    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    private async Task ServeAsync(TcpClient connection)
    {
        using (connection)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var stream = connection.GetStream();
                var request = await ReadHeadAsync(stream, timeout.Token).ConfigureAwait(false);
                var (status, body) = Handle(request);
                var payload = Encoding.UTF8.GetBytes(body);
                var head = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {status}\r\nContent-Type: {(status.StartsWith("200", StringComparison.Ordinal) ? "application/json" : "text/plain")}; charset=utf-8\r\n" +
                    $"Content-Length: {payload.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(head, timeout.Token).ConfigureAwait(false);
                await stream.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A broken or slow client must not affect the app.
            }
        }
    }

    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    private (string Status, string Body) Handle(string? request)
    {
        if (request is null)
        {
            return ("400 Bad Request", "Bad request.");
        }

        var lines = request.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length < 3)
        {
            return ("400 Bad Request", "Bad request.");
        }

        if (!string.Equals(parts[0], "GET", StringComparison.Ordinal))
        {
            return ("405 Method Not Allowed", "Only GET is supported.");
        }

        var target = parts[1];
        var q = target.IndexOf('?', StringComparison.Ordinal);
        var query = q >= 0 ? target[(q + 1)..] : "";
        var path = q >= 0 ? target[..q] : target;
        if (_token is not null && !Authorized(lines, query))
        {
            return ("401 Unauthorized", "A valid access token is required.");
        }

        return path is "/" or "/export" ? ("200 OK", _devTools.ExportJson()) : ("404 Not Found", "Not found.");
    }

    private bool Authorized(string[] lines, string query)
    {
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (pair.StartsWith("token=", StringComparison.Ordinal) && Matches(Uri.UnescapeDataString(pair[6..])))
            {
                return true;
            }
        }

        foreach (var line in lines.Skip(1))
        {
            if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase) &&
                line[14..].Trim() is var value && value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
                Matches(value[7..].Trim()))
            {
                return true;
            }
        }

        return false;
    }

    private bool Matches(string candidate) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), Encoding.UTF8.GetBytes(_token!));

    private static async Task<string?> ReadHeadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxRequestBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            length += read;
            var text = Encoding.ASCII.GetString(buffer, 0, length);
            if (text.IndexOf("\r\n\r\n", StringComparison.Ordinal) is var end and >= 0)
            {
                return text[..end];
            }
        }

        return null;
    }
}
