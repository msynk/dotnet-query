using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using TodoApp.Client;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// In the browser: one client (cache) per tab, and todos come from the server's API.
builder.Services.AddDotNetQueryBlazor(options => options.DefaultStaleTime = TimeSpan.FromSeconds(10));
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<ITodoClient, HttpTodoClient>();

await builder.Build().RunAsync();
