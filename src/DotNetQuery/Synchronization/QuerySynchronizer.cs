using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace DotNetQuery.Synchronization;

/// <summary>
/// A transport for <see cref="QuerySynchronizer"/> messages between caches - browser tabs (BroadcastChannel),
/// windows of a desktop app, processes (named pipes, Redis pub/sub) or servers (SignalR). Messages are opaque
/// strings; a channel delivers each posted message to every other participant (delivering it back to the sender is
/// harmless - senders ignore their own messages).
/// </summary>
public interface IQuerySyncChannel
{
    /// <summary>Raised for every message received from another participant.</summary>
    event Action<string>? MessageReceived;

    /// <summary>Sends <paramref name="message"/> to the other participants.</summary>
    Task PostAsync(string message, CancellationToken cancellationToken = default);
}

/// <summary>An in-process <see cref="IQuerySyncChannel"/> hub: every channel it creates receives every other channel's messages.</summary>
/// <remarks>Useful for several clients in one process (for example one per window) and for tests.</remarks>
public sealed class InMemoryQuerySyncHub
{
    private readonly List<Channel> _channels = [];

    /// <summary>Creates a channel connected to every other channel of this hub.</summary>
    public IQuerySyncChannel CreateChannel()
    {
        var channel = new Channel(this);
        lock (_channels)
        {
            _channels.Add(channel);
        }

        return channel;
    }

    private void Broadcast(Channel sender, string message)
    {
        Channel[] targets;
        lock (_channels)
        {
            targets = [.. _channels];
        }

        foreach (var target in targets)
        {
            if (!ReferenceEquals(target, sender))
            {
                target.Deliver(message);
            }
        }
    }

    private sealed class Channel(InMemoryQuerySyncHub hub) : IQuerySyncChannel
    {
        public event Action<string>? MessageReceived;

        public Task PostAsync(string message, CancellationToken cancellationToken = default)
        {
            hub.Broadcast(this, message);
            return Task.CompletedTask;
        }

        public void Deliver(string message) => MessageReceived?.Invoke(message);
    }
}

/// <summary>Options for a <see cref="QuerySynchronizer"/>.</summary>
public sealed record QuerySyncOptions
{
    /// <summary>Serialization options for reflection-based registrations. Defaults to <see cref="JsonSerializerDefaults.Web"/>.</summary>
    public JsonSerializerOptions? JsonOptions { get; init; }

    /// <summary>
    /// Only messages with the same channel name are applied - for example an app version, so tabs running an old
    /// build ignore payloads with a different shape. Defaults to <c>"default"</c>.
    /// </summary>
    public string ChannelName { get; init; } = "default";
}

/// <summary>
/// Keeps registered queries consistent across several caches (browser tabs, windows, processes) over an
/// <see cref="IQuerySyncChannel"/>: data written in one cache (a fetch, <c>SetQueryData</c>, an optimistic update or
/// its rollback) appears in the others, and invalidating a registered query invalidates it everywhere. Removals
/// are not synchronized (garbage collection removes entries too).
/// </summary>
/// <remarks>
/// <para>
/// Register the queries to synchronize, like persistence: exact keys with <see cref="Register{T}(QueryKey, JsonTypeInfo{T})"/>,
/// or every query under a prefix with <see cref="RegisterPrefix{T}(QueryKey, JsonTypeInfo{T})"/> (prefix updates are
/// applied only to matching queries the receiving cache already holds, since a key cannot be rebuilt from text).
/// Then call <see cref="Start"/>.
/// </para>
/// <para>
/// Conflicts resolve by timestamp (last writer wins), so caches converge; a sender's own later message about a key
/// it last wrote (for example rolling back an optimistic update to an older snapshot) is always applied. Messages are
/// numbered per sender, so a transport that delivers them out of order cannot apply an older one over a newer one.
/// Messages carry serialized data - only synchronize data every participant is allowed to see.
/// </para>
/// </remarks>
public sealed class QuerySynchronizer : IDisposable
{
    private static readonly JsonSerializerOptions DefaultJson = new(JsonSerializerDefaults.Web);

    private readonly IQueryClient _client;
    private readonly IQuerySyncChannel _channel;
    private readonly QuerySyncOptions _options;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly string _sender = Guid.NewGuid().ToString("N");
    private readonly ConcurrentDictionary<QueryKey, Registration> _exact = new();
    private readonly ConcurrentDictionary<string, Registration> _exactByText = new(StringComparer.Ordinal);
    private readonly List<Registration> _prefixes = [];
    private readonly ConcurrentDictionary<QueryKey, (string Sender, DateTimeOffset UpdatedAt, long? Sequence)> _remote = new();
    private readonly object _remoteGate = new();
    private readonly ConcurrentDictionary<QueryKey, int> _applyingInvalidations = new();

    // Cached keys under a prefix registration, by text, so a received message finds its query without formatting
    // every cached key.
    private readonly ConcurrentDictionary<string, QueryKey> _prefixKeysByText = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<QueryKey, string> _prefixKeyTexts = new();
    private readonly Action<QueryCacheChange> _cacheHandler;
    private readonly Action<DotNetQueryEvent> _eventHandler;
    private readonly Action<string> _messageHandler;
    private long _sequence;
    private int _started;
    private int _disposed;

    /// <summary>Creates a synchronizer between <paramref name="client"/> and the other participants of <paramref name="channel"/>.</summary>
    public QuerySynchronizer(IQueryClient client, IQuerySyncChannel channel, QuerySyncOptions? options = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _options = options ?? new QuerySyncOptions();
        _jsonOptions = _options.JsonOptions ?? DefaultJson;
        _cacheHandler = OnCacheChanged;
        _eventHandler = OnDiagnosticEvent;
        _messageHandler = OnMessage;
    }

    /// <summary>Raised when a message cannot be sent, read or applied. The message is skipped.</summary>
    public event Action<Exception>? Error;

    /// <summary>Synchronizes the query under <paramref name="key"/>, serialized with <paramref name="typeInfo"/>. Trim/AOT-safe.</summary>
    public QuerySynchronizer Register<T>(QueryKey key, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(typeInfo);
        var registration = Registration.Create(key, isPrefix: false, data => JsonSerializer.Serialize(data, typeInfo), json => JsonSerializer.Deserialize(json, typeInfo)!);
        _exact[key] = registration;
        _exactByText[key.ToString()] = registration;
        return this;
    }

    /// <summary>Synchronizes the query under <paramref name="key"/> with reflection-based JSON.</summary>
    [RequiresUnreferencedCode("JSON serialization of T may require types that cannot be statically analyzed. Use the JsonTypeInfo<T> overload.")]
    [RequiresDynamicCode("JSON serialization of T may require runtime code generation. Use the JsonTypeInfo<T> overload.")]
    public QuerySynchronizer Register<T>(QueryKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var json = _jsonOptions;
        var registration = Registration.Create(key, isPrefix: false, data => JsonSerializer.Serialize(data, json), text => JsonSerializer.Deserialize<T>(text, json)!);
        _exact[key] = registration;
        _exactByText[key.ToString()] = registration;
        return this;
    }

    /// <summary>Synchronizes a query definition's key, serialized with <paramref name="typeInfo"/>.</summary>
    public QuerySynchronizer Register<T>(QueryDefinition<T> query, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Register(query.Key, typeInfo);
    }

    /// <summary>
    /// Synchronizes every query of type <typeparamref name="T"/> whose key starts with <paramref name="prefix"/>.
    /// Received updates apply only to matching queries the receiving cache already holds.
    /// </summary>
    public QuerySynchronizer RegisterPrefix<T>(QueryKey prefix, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(typeInfo);
        lock (_prefixes)
        {
            _prefixes.Add(Registration.Create(prefix, isPrefix: true, data => JsonSerializer.Serialize(data, typeInfo), json => JsonSerializer.Deserialize(json, typeInfo)!));
        }

        if (Volatile.Read(ref _started) != 0)
        {
            TrackCachedPrefixKeys();
        }

        return this;
    }

    /// <summary>Starts sending local changes and applying received ones. Idempotent.</summary>
    public QuerySynchronizer Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            _client.Cache.Changed += _cacheHandler;
            _client.DiagnosticEvent += _eventHandler;
            _channel.MessageReceived += _messageHandler;

            // After subscribing, so a query added meanwhile is tracked by one or the other.
            TrackCachedPrefixKeys();
        }

        return this;
    }

    /// <summary>Stops synchronizing.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && Volatile.Read(ref _started) != 0)
        {
            _client.Cache.Changed -= _cacheHandler;
            _client.DiagnosticEvent -= _eventHandler;
            _channel.MessageReceived -= _messageHandler;
        }
    }

    // ------------------------------------------------------------------ outgoing

    private void OnCacheChanged(QueryCacheChange change)
    {
        // Echoes are recognized by the (sender, timestamp) of the data this cache last received for the key - not by
        // thread, because the applying thread may also deliver notifications of unrelated local changes.
        if (Find(change.Key) is not { } registration)
        {
            return;
        }

        if (registration.IsPrefix)
        {
            TrackPrefixKey(change.Key, removed: change.Kind is QueryCacheChangeKind.Removed);
        }

        // Removals are not synchronized: garbage collection removes entries too, and that is local memory
        // management, not a change other caches should copy.
        if (change.Kind is QueryCacheChangeKind.Removed)
        {
            _remote.TryRemove(change.Key, out _);
            return;
        }

        if (change.Kind is not QueryCacheChangeKind.DataUpdated || registration.Snapshot(_client, change.Key) is not { } snapshot)
        {
            return;
        }

        // An echo of data this cache just received: do not send it back.
        if (_remote.TryGetValue(change.Key, out var applied) && applied.UpdatedAt == snapshot.UpdatedAt)
        {
            return;
        }

        _remote.TryRemove(change.Key, out _);
        Post(new SyncEnvelope { Kind = SyncKind.Data, Key = change.Key.ToString(), Payload = snapshot.Payload, UpdatedAt = snapshot.UpdatedAt });
    }

    private void OnDiagnosticEvent(DotNetQueryEvent diagnostic)
    {
        if (diagnostic is not QueryInvalidatedEvent invalidated)
        {
            return;
        }

        foreach (var key in invalidated.Keys)
        {
            // Skip the invalidation this cache is applying on another participant's behalf.
            if (!_applyingInvalidations.ContainsKey(key) && Find(key) is not null)
            {
                Post(new SyncEnvelope { Kind = SyncKind.Invalidated, Key = key.ToString() });
            }
        }
    }

    private void Post(SyncEnvelope envelope)
    {
        envelope.Sender = _sender;
        envelope.Channel = _options.ChannelName;
        envelope.Sequence = Interlocked.Increment(ref _sequence);
        _ = SendAsync(JsonSerializer.Serialize(envelope, SyncJsonContext.Default.SyncEnvelope));
    }

    private async Task SendAsync(string message)
    {
        try
        {
            await _channel.PostAsync(message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RaiseError(ex);
        }
    }

    // ------------------------------------------------------------------ incoming

    private void OnMessage(string message)
    {
        SyncEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize(message, SyncJsonContext.Default.SyncEnvelope);
        }
        catch (JsonException ex)
        {
            RaiseError(ex);
            return;
        }

        if (envelope is null || envelope.Sender == _sender || !string.Equals(envelope.Channel, _options.ChannelName, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            foreach (var (key, registration) in Resolve(envelope.Key))
            {
                switch (envelope.Kind)
                {
                    case SyncKind.Data when envelope.Payload is { } payload && envelope.UpdatedAt is { } updatedAt:
                        ApplyData(key, registration, envelope.Sender!, envelope.Sequence, payload, updatedAt);
                        break;
                    case SyncKind.Invalidated:
                        ApplyInvalidation(key);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            RaiseError(ex);
        }
    }

    private void ApplyInvalidation(QueryKey key)
    {
        // The invalidation event is raised synchronously inside InvalidateQueriesAsync, before its first await.
        _applyingInvalidations.AddOrUpdate(key, 1, static (_, count) => count + 1);
        try
        {
            _ = _client.InvalidateQueriesAsync(k => k == key);
        }
        finally
        {
            if (_applyingInvalidations.AddOrUpdate(key, 0, static (_, count) => count - 1) <= 0)
            {
                _applyingInvalidations.TryRemove(KeyValuePair.Create(key, 0));
            }
        }
    }

    private void ApplyData(QueryKey key, Registration registration, string sender, long? sequence, string payload, DateTimeOffset updatedAt)
    {
        // Last writer wins, decided atomically by the entry; the sender of the current value may always revise it
        // (for example a rollback to an older snapshot) - with a later message only: sends are not serialized, so a
        // transport may deliver a sender's messages out of order, and an older one must not overwrite a newer one.
        var stamp = (sender, updatedAt, sequence);
        bool force;
        lock (_remoteGate)
        {
            force = _remote.TryGetValue(key, out var last) && last.Sender == sender;
            if (force && sequence is { } current && last.Sequence is { } applied && current <= applied)
            {
                return; // delivered late
            }

            // Record the stamp before the write: its change notification may be delivered on any thread, and it must
            // be recognized as an echo there.
            _remote[key] = stamp;
        }

        if (!registration.Apply(_client, key, payload, updatedAt, force))
        {
            // Not applied (older than local data): forget the stamp unless a newer message replaced it meanwhile.
            _remote.TryRemove(KeyValuePair.Create(key, stamp));
        }
    }

    private IEnumerable<(QueryKey Key, Registration Registration)> Resolve(string? keyText)
    {
        if (keyText is null)
        {
            yield break;
        }

        if (_exactByText.TryGetValue(keyText, out var exact))
        {
            yield return (exact.Key, exact);
            yield break;
        }

        if (_prefixKeysByText.TryGetValue(keyText, out var key) && FindPrefix(key) is { } prefix)
        {
            yield return (key, prefix);
        }
    }

    private Registration? Find(QueryKey key) => _exact.TryGetValue(key, out var exact) ? exact : FindPrefix(key);

    private Registration? FindPrefix(QueryKey key)
    {
        lock (_prefixes)
        {
            foreach (var prefix in _prefixes)
            {
                if (prefix.Key.IsPrefixOf(key))
                {
                    return prefix;
                }
            }
        }

        return null;
    }

    private void TrackCachedPrefixKeys()
    {
        lock (_prefixes)
        {
            if (_prefixes.Count == 0)
            {
                return;
            }
        }

        foreach (var key in _client.Cache.Keys)
        {
            if (Find(key) is { IsPrefix: true })
            {
                TrackPrefixKey(key, removed: false);
            }
        }
    }

    // Each key is formatted once while it is cached.
    private void TrackPrefixKey(QueryKey key, bool removed)
    {
        if (!removed)
        {
            if (!_prefixKeyTexts.ContainsKey(key))
            {
                var text = key.ToString();
                _prefixKeyTexts[key] = text;
                _prefixKeysByText[text] = key;
            }

            return;
        }

        // A key removed and added again at once may report the two out of order: keep it while it is cached.
        if (!_client.Cache.TryGetEntry(key, out _) && _prefixKeyTexts.TryRemove(key, out var removedText))
        {
            _prefixKeysByText.TryRemove(KeyValuePair.Create(removedText, key));
        }
    }

    private void RaiseError(Exception error)
    {
        try
        {
            Error?.Invoke(error);
        }
        catch (Exception)
        {
            // Subscriber errors must not break synchronization.
        }
    }

    private sealed class Registration
    {
        private Registration(QueryKey key, bool isPrefix)
        {
            Key = key;
            IsPrefix = isPrefix;
        }

        public QueryKey Key { get; }

        public bool IsPrefix { get; }

        public Func<IQueryClient, QueryKey, (string Payload, DateTimeOffset UpdatedAt)?> Snapshot { get; private init; } = default!;

        public Func<IQueryClient, QueryKey, string, DateTimeOffset, bool, bool> Apply { get; private init; } = default!;

        public static Registration Create<T>(QueryKey key, bool isPrefix, Func<T, string> serialize, Func<string, T> deserialize) =>
            new(key, isPrefix)
            {
                Snapshot = (client, target) =>
                    client.Cache.TryGetState<T>(target, out var state) && state is { HasData: true, DataUpdatedAt: { } at }
                        ? (serialize(state.Data!), at)
                        : null,
                Apply = (client, target, payload, updatedAt, force) =>
                {
                    var data = deserialize(payload);
                    if (isPrefix)
                    {
                        // Only queries this cache already holds (and of the registered type).
                        return client.Cache.TryGetEntry(target, out var entry) && entry is QueryEntry<T> typed
                            && typed.TryApplyRemote(data, updatedAt, force) == true;
                    }

                    while (true)
                    {
                        if (client.Cache.GetOrCreate<T>(target).TryApplyRemote(data, updatedAt, force) is { } applied)
                        {
                            return applied;
                        }
                    }
                },
            };
    }
}

internal enum SyncKind
{
    Data,
    Invalidated,
}

internal sealed class SyncEnvelope
{
    public string? Channel { get; set; }

    public string? Sender { get; set; }

    // Numbers the sender's messages, so a receiver can tell a late one from a later one.
    public long? Sequence { get; set; }

    public SyncKind Kind { get; set; }

    public string? Key { get; set; }

    public string? Payload { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(SyncEnvelope))]
internal sealed partial class SyncJsonContext : JsonSerializerContext;
