using System.Collections.Concurrent;
using System.Diagnostics;
using DotNetQuery.Synchronization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DotNetQuery.SignalR.Tests;

public sealed record Todo(int Id, string Title, long Version = 1);

/// <summary>The server's event history, so a client can replay what it missed.</summary>
public sealed class EventLog
{
    private readonly List<QueryEventEnvelope<Todo>> _events = [];
    private long _sequence;

    public QueryEventEnvelope<Todo> Append(Todo todo)
    {
        lock (_events)
        {
            var envelope = new QueryEventEnvelope<Todo>
            {
                EventId = Guid.NewGuid().ToString("N"),
                EventType = "TodoChanged",
                EntityId = todo.Id.ToString(),
                Version = todo.Version,
                Sequence = ++_sequence,
                Payload = todo,
            };
            _events.Add(envelope);
            return envelope;
        }
    }

    public List<QueryEventEnvelope<Todo>> Since(long sequence)
    {
        lock (_events)
        {
            return _events.Where(e => e.Sequence > sequence).ToList();
        }
    }
}

public sealed class TodoHub(EventLog log) : Hub
{
    public static readonly ConcurrentDictionary<string, int> Joins = new();

    public async Task Join(string group)
    {
        if (group == "forbidden")
        {
            throw new HubException("Not allowed to join this group.");
        }

        Joins.AddOrUpdate(group, 1, (_, n) => n + 1);
        await Groups.AddToGroupAsync(Context.ConnectionId, group);
    }

    public List<QueryEventEnvelope<Todo>> EventsSince(long sequence) => log.Since(sequence);
}

/// <summary>A switch between the client and the in-process server: breaking it fails every request in flight and every new one.</summary>
public sealed class Network
{
    private CancellationTokenSource _down = new();

    public bool IsBroken { get; private set; }

    public CancellationToken DownToken => _down.Token;

    public void Break()
    {
        IsBroken = true;
        var previous = _down;
        _down = new CancellationTokenSource();
        previous.Cancel();
    }

    public void Restore() => IsBroken = false;
}

internal sealed class NetworkHandler(Network network, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (network.IsBroken)
        {
            throw new HttpRequestException("network down");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, network.DownToken);
        try
        {
            return await base.SendAsync(request, linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException("network down");
        }
    }
}

internal sealed class ForeverRetry : IRetryPolicy
{
    public TimeSpan? NextRetryDelay(RetryContext retryContext) => TimeSpan.FromMilliseconds(50);
}

/// <summary>An in-process ASP.NET Core server hosting <see cref="TodoHub"/>.</summary>
public sealed class HubServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly List<HubConnection> _connections = [];

    private HubServer(WebApplication app)
    {
        _app = app;
        Server = app.GetTestServer();
        Hub = app.Services.GetRequiredService<IHubContext<TodoHub>>();
        Log = app.Services.GetRequiredService<EventLog>();
    }

    public TestServer Server { get; }

    public IHubContext<TodoHub> Hub { get; }

    public EventLog Log { get; }

    public Network Network { get; } = new();

    public static async Task<HubServer> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR();
        builder.Services.AddSingleton<EventLog>();
        var app = builder.Build();
        app.MapHub<TodoHub>("/hub");
        await app.StartAsync();
        return new HubServer(app);
    }

    public HubConnection CreateConnection(bool automaticReconnect = true)
    {
        var builder = new HubConnectionBuilder()
            .WithUrl("http://localhost/hub", options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => new NetworkHandler(Network, Server.CreateHandler());
            });
        if (automaticReconnect)
        {
            builder.WithAutomaticReconnect(new ForeverRetry());
        }

        var connection = builder.Build();
        _connections.Add(connection);
        return connection;
    }

    /// <summary>Publishes a todo change to every client (or to a group), recording it in the history.</summary>
    public async Task<QueryEventEnvelope<Todo>> PublishAsync(Todo todo, string? group = null)
    {
        var envelope = Log.Append(todo);
        var clients = group is null ? Hub.Clients.All : Hub.Clients.Group(group);
        await clients.SendAsync("TodoChanged", envelope);
        return envelope;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            try
            {
                await connection.DisposeAsync();
            }
            catch (Exception)
            {
            }
        }

        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 15000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException("Condition was not met within the timeout.");
            }

            await Task.Delay(10);
        }
    }
}
