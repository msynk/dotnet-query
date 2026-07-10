using Microsoft.Extensions.DependencyInjection;
using TodoApp;
using TodoApp.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// One line wires up DotNetQuery + the Blazor browser-signal bridge.
builder.Services.AddDotNetQueryBlazor(options =>
{
    options.DefaultStaleTime = TimeSpan.FromSeconds(10);
    options.DefaultRetryCount = 2;
});

builder.Services.AddSingleton<TodoApi>();

var app = builder.Build();

app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
