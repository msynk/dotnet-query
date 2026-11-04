using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotNetQuery.Synchronization;

/// <summary>Options for a <see cref="QueryEventRelay"/>.</summary>
public sealed record QueryEventRelayOptions
{
    /// <summary>
    /// Only messages with the same channel name are applied - use the application or schema version, so participants
    /// running different builds (whose mappings may differ) ignore each other. Defaults to <c>"default"</c>.
    /// </summary>
    public string ChannelName { get; init; } = "default";

    /// <summary>How many received message ids are remembered to discard duplicates. Defaults to 1,000.</summary>
    public int DeduplicationCapacity { get; init; } = 1_000;

    /// <summary>How long a received message id is remembered. Defaults to 5 minutes.</summary>
    public TimeSpan DeduplicationWindow { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Relays a <see cref="QueryEventSynchronizer"/>'s processed events to other participants of an
/// <see cref="IQuerySyncChannel"/> - browser tabs over <c>BroadcastChannel</c> (<c>AddCrossTabEventRelay</c> in
/// <c>Msynk.DotNetQuery.Blazor</c>), windows of a desktop app - as small invalidation notices, so a participant without
/// its own connection (or whose connection lagged) refreshes the queries those events affect.
/// </summary>
/// <remarks>
/// <para>
/// A notice carries no payload, no data and no keys: only the event name (and its id, when it has one), the
/// synchronizer's scope and a schema version. A receiver invalidates what its own mappings of that event cover - so
/// nothing a message says can make it touch other queries - and a recovery on one side tells the others to invalidate
/// everything covered. Notices received are never relayed again; messages from the participant itself, from another
/// scope, of another channel name, malformed or already seen are ignored. A receiver that already processed the same
/// event id itself skips the notice.
/// </para>
/// <para>
/// Relaying coordinates freshness between participants; it is an optimization, not a consistency mechanism - each
/// participant still reconciles with the server on its own. Participants must share the signed-in user: relay only
/// within one authentication session (tabs of one browser profile), and change the synchronizer's scope (or dispose the
/// relay) when the user or tenant changes.
/// </para>
/// </remarks>
public sealed class QueryEventRelay : IDisposable
{
    private const int MaxEventNameLength = 256;
    private const int MaxIdLength = 128;

    private readonly QueryEventSynchronizer _synchronizer;
    private readonly IQuerySyncChannel _channel;
    private readonly QueryEventRelayOptions _options;
    private readonly string _origin = Guid.NewGuid().ToString("N");
    private readonly ExpiringIdSet _seen;
    private readonly Action<string> _messageHandler;
    private long _sent;
    private long _applied;
    private long _ignored;
    private int _started;
    private int _disposed;

    /// <summary>Creates a relay between <paramref name="synchronizer"/> and the other participants of <paramref name="channel"/>.</summary>
    public QueryEventRelay(QueryEventSynchronizer synchronizer, IQuerySyncChannel channel, QueryEventRelayOptions? options = null)
    {
        _synchronizer = synchronizer ?? throw new ArgumentNullException(nameof(synchronizer));
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _options = options ?? new QueryEventRelayOptions();
        ArgumentException.ThrowIfNullOrEmpty(_options.ChannelName, nameof(options));
        _seen = new ExpiringIdSet(_options.DeduplicationCapacity, _options.DeduplicationWindow, synchronizer.Client.Cache.Runtime.TimeProvider);
        _messageHandler = OnMessage;
    }

    /// <summary>Raised when a message cannot be sent or is malformed. The message is skipped.</summary>
    public event Action<Exception>? Error;

    /// <summary>Gets how many notices this relay sent.</summary>
    public long MessagesSent => Interlocked.Read(ref _sent);

    /// <summary>Gets how many received notices were applied.</summary>
    public long MessagesApplied => Interlocked.Read(ref _applied);

    /// <summary>Gets how many received messages were ignored (own, duplicate, other scope or channel, malformed).</summary>
    public long MessagesIgnored => Interlocked.Read(ref _ignored);

    /// <summary>Starts relaying in both directions. Idempotent. A synchronizer has at most one relay.</summary>
    public QueryEventRelay Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            _synchronizer.AttachRelay(this);
            _channel.MessageReceived += _messageHandler;
        }

        return this;
    }

    /// <summary>Stops relaying. The channel is not disposed.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && Volatile.Read(ref _started) != 0)
        {
            _channel.MessageReceived -= _messageHandler;
            _synchronizer.DetachRelay(this);
        }
    }

    // ------------------------------------------------------------------ outgoing (called by the synchronizer's loop)

    internal void OnEventProcessed(string eventName, string? eventId) =>
        Post(new RelayMessage { Operation = RelayOperation.Event, EventName = eventName, EventId = eventId });

    internal void OnResynchronized() => Post(new RelayMessage { Operation = RelayOperation.Resync });

    private void Post(RelayMessage message)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        message.Version = RelayMessage.SchemaVersion;
        message.Channel = _options.ChannelName;
        message.Origin = _origin;
        message.Id = Guid.NewGuid().ToString("N");
        message.Scope = _synchronizer.Scope;
        Interlocked.Increment(ref _sent);
        _ = SendAsync(JsonSerializer.Serialize(message, RelayJsonContext.Default.RelayMessage));
    }

    private async Task SendAsync(string text)
    {
        try
        {
            await _channel.PostAsync(text).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RaiseError(ex);
        }
    }

    // ------------------------------------------------------------------ incoming

    private void OnMessage(string text)
    {
        RelayMessage? message;
        try
        {
            message = JsonSerializer.Deserialize(text, RelayJsonContext.Default.RelayMessage);
        }
        catch (JsonException ex)
        {
            Ignore();
            RaiseError(ex);
            return;
        }

        if (message is null || !IsValid(message))
        {
            Ignore();
            return;
        }

        // Another participant's echo of our own notice, another build, or another tenant or user session.
        if (message.Origin == _origin
            || !string.Equals(message.Channel, _options.ChannelName, StringComparison.Ordinal)
            || !string.Equals(message.Scope, _synchronizer.Scope, StringComparison.Ordinal))
        {
            Ignore();
            return;
        }

        lock (_seen)
        {
            if (_seen.Contains(message.Id!))
            {
                Ignore();
                return;
            }

            _seen.Add(message.Id!);
        }

        // Applied without relaying it again (loop prevention): only the synchronizer's own processing posts notices.
        _synchronizer.ApplyRelayed(message.Operation is RelayOperation.Event ? message.EventName : null, message.EventId);
        Interlocked.Increment(ref _applied);
    }

    private static bool IsValid(RelayMessage message) =>
        message.Version == RelayMessage.SchemaVersion
        && message.Id is { Length: > 0 and <= MaxIdLength }
        && message.Origin is { Length: > 0 and <= MaxIdLength }
        && message.EventId is null or { Length: <= MaxIdLength }
        && message.Operation switch
        {
            RelayOperation.Event => message.EventName is { Length: > 0 and <= MaxEventNameLength },
            RelayOperation.Resync => true,
            _ => false,
        };

    private void Ignore() => Interlocked.Increment(ref _ignored);

    private void RaiseError(Exception error)
    {
        try
        {
            Error?.Invoke(error);
        }
        catch (Exception)
        {
            // Subscriber errors must not break relaying.
        }
    }
}

internal enum RelayOperation
{
    Event = 1,
    Resync = 2,
}

internal sealed class RelayMessage
{
    public const int SchemaVersion = 1;

    [JsonPropertyName("v")]
    public int Version { get; set; }

    [JsonPropertyName("ch")]
    public string? Channel { get; set; }

    [JsonPropertyName("o")]
    public string? Origin { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("op")]
    public RelayOperation Operation { get; set; }

    [JsonPropertyName("ev")]
    public string? EventName { get; set; }

    [JsonPropertyName("eid")]
    public string? EventId { get; set; }

    [JsonPropertyName("sc")]
    public string? Scope { get; set; }
}

[JsonSourceGenerationOptions(UseStringEnumConverter = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RelayMessage))]
internal sealed partial class RelayJsonContext : JsonSerializerContext;
