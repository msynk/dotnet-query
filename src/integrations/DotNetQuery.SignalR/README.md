# Msynk.DotNetQuery.SignalR

SignalR transport for DotNetQuery's real-time cache synchronization. `HubConnectionQueryEventSource` feeds typed hub
events into the core's `QueryEventSynchronizer`, reports the connection lifecycle (connecting, reconnecting,
resubscribing, connected, disconnected), restores server-side subscriptions such as group memberships after every
reconnect, and retries failed starts per an optional policy. It never disposes a `HubConnection` it does not own.

```csharp
builder.Services.AddDotNetQuery()
    .AddSignalRSync(
        _ => new HubConnectionBuilder().WithUrl(hubUrl).WithAutomaticReconnect().Build(),
        sync => sync.Map<QueryEventEnvelope<Todo>>("TodoChanged", m => m
            .Upsert(TodoQueries.Entity, e => e.Payload)
            .Invalidate(TodoQueries.Stats)),
        new HubConnectionQueryEventSourceOptions
        {
            OwnsConnection = true,
            OnConnected = (hub, ct) => hub.InvokeAsync("JoinTenant", tenantId, ct),
        });

await app.Services.GetRequiredService<QueryEventSynchronizer>().StartAsync();
```

Targets .NET 8 and .NET 10; trim- and NativeAOT-compatible when payloads use source-generated JSON
(`AddJsonProtocol(o => o.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, AppJson.Default))`).

See [Real-time synchronization](../../../docs/realtime-sync.md) for mappings, ordering, recovery and limitations.
