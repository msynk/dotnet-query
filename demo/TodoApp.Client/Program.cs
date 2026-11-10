using DotNetQuery.SignalR;
using DotNetQuery.Synchronization;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.AspNetCore.SignalR.Client;
using TodoApp.Client;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
var baseAddress = new Uri(builder.HostEnvironment.BaseAddress);

// In the browser: one client (cache) per tab, and todos come from the server's API.
builder.Services.AddDotNetQueryBlazor(options => options.DefaultStaleTime = TimeSpan.FromSeconds(10))
    // Live updates: the server pushes todo events over SignalR; each one invalidates the cached list, so the open page
    // refetches it (an invalidation never conflicts with the page's own optimistic add). <LiveUpdates /> starts it.
    .AddSignalRSync(
        _ => new HubConnectionBuilder()
            .WithUrl(new Uri(baseAddress, TodoEvents.HubPath.TrimStart('/')))
            .WithAutomaticReconnect()
            .AddJsonProtocol(o => o.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, TodoClientJson.Default)) // trim-safe payloads
            .Build(),
        sync => sync.Map<QueryEventEnvelope<TodoItem>>(TodoEvents.Added, m => m.Invalidate(ClientTodoQueries.All)),
        new HubConnectionQueryEventSourceOptions
        {
            OwnsConnection = true,
            RestartDelay = (attempt, _) => attempt < 10 ? TimeSpan.FromSeconds(Math.Min(30, 1 << attempt)) : null,
        },
        new QueryEventSyncOptions
        {
            Name = "todos",
            // The list was just hydrated from the prerender; the first connection has nothing to reconcile. Reconnects
            // (missed events) still invalidate the list.
            RecoverOnConnect = false,
        })
    // Tabs share what one of them learned (for example while another tab's connection was reconnecting).
    .AddCrossTabEventRelay(new QueryEventRelayOptions { ChannelName = "todoapp-v1" });
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = baseAddress });
builder.Services.AddScoped<ITodoClient, HttpTodoClient>();

await builder.Build().RunAsync();
