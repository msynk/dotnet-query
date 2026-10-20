using Microsoft.Extensions.DependencyInjection;
using TodoApp;
using TodoApp.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// One line wires up DotNetQuery + the Blazor browser-signal bridge. The client is scoped: one private cache
// per user circuit, so cached data is never shared between users of this server.
builder.Services.AddDotNetQueryBlazor(options =>
{
    options.DefaultStaleTime = TimeSpan.FromSeconds(10);
    options.DefaultRetryCount = 2;
});

builder.Services.AddSingleton<TodoApi>();

var app = builder.Build();

app.UseAntiforgery();
app.MapStaticAssets(); // serves _framework/blazor.web.js and library assets in every environment
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
