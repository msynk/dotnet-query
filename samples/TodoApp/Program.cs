using DotNetQuery;
using DotNetQuery.Synchronization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using TodoApp;
using TodoApp.Client;
using TodoApp.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

// One line wires up DotNetQuery + the Blazor browser-signal bridge. The client is scoped: one private cache per user
// circuit, so cached data is never shared between users of this server.
builder.Services.AddDotNetQueryBlazor(options =>
    {
        options.DefaultStaleTime = TimeSpan.FromSeconds(10);
        options.DefaultRetryCount = 2;
        options.DefaultShouldRetry = HttpRetryPolicy.ShouldRetry;
    })
    // Keep every todo list in sync across browser tabs: a change made in one tab shows up in the others.
    .AddCrossTabSync(sync => sync.RegisterPrefix(TodoQueries.All, TodoJson.Default.ListTodo))
    // Blazor Server: when a dropped circuit connection comes back, refetch the stale queries on screen.
    .RefetchOnCircuitReconnect()
    // Write fetch failures, retries and mutations to the app's logs.
    .AddDiagnosticLogging();

builder.Services.AddSingleton<TodoApi>();
builder.Services.AddScoped<ITodoClient, ServerTodoClient>();   // prerendering of the WebAssembly/Auto pages

// Real-time: the WebAssembly pages subscribe to todo events and invalidate their cached list when another user (or
// device, or a script calling the API) changes it.
builder.Services.AddSignalR();

var app = builder.Build();

app.UseAntiforgery();
app.MapStaticAssets(); // serves _framework/blazor.web.js and library assets in every environment

// The API the WebAssembly pages call from the browser.
var api = app.MapGroup("/api/todos");
api.MapGet("/", async (TodoApi todos, CancellationToken ct) =>
    (await todos.GetAllAsync(TodoFilter.All, ct)).Select(t => new TodoItem(t.Id, t.Title, t.Done)));
api.MapPost("/", async (TodoApi todos, IHubContext<TodoEventsHub> events, [Microsoft.AspNetCore.Mvc.FromBody] string title, CancellationToken ct) =>
{
    var todo = await todos.AddAsync(title, ct);
    var item = new TodoItem(todo.Id, todo.Title, todo.Done);
    await events.Clients.All.SendAsync(TodoEvents.Added, new QueryEventEnvelope<TodoItem>
    {
        EventId = Guid.NewGuid().ToString("N"),
        EntityId = item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Payload = item,
    }, ct);
    return Results.Created($"/api/todos/{todo.Id}", item);
});
app.MapHub<TodoEventsHub>(TodoEvents.HubPath);

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(ITodoClient).Assembly);

app.Run();

/// <summary>The hub the WebAssembly pages listen to; the server pushes todo events through it.</summary>
internal sealed class TodoEventsHub : Hub;
