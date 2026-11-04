using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Security.Claims;
using DotNetQuery;
using DotNetQuery.Blazor;
using DotNetQuery.Synchronization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registration extensions for the DotNetQuery Blazor adapter.</summary>
public static class DotNetQueryBlazorServiceCollectionExtensions
{
    /// <summary>
    /// Registers DotNetQuery for Blazor: a <b>scoped</b> <see cref="IQueryClient"/> together with the browser
    /// signal bridge that forwards window focus, online/offline and visibility events to the client.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Scoped is the correct lifetime for every Blazor hosting model. In Blazor WebAssembly a scope lives for
    /// the whole app, so this is one cache per browser tab. In Blazor Server / interactive server rendering a
    /// scope is one user circuit, so each user gets a private cache - a singleton would share cached data
    /// (and connectivity state) between all users of the server.
    /// </para>
    /// <para>
    /// Because prerendering runs in a different scope than the interactive circuit, the <c>&lt;Query&gt;</c>
    /// component persists the data it fetched during prerendering and hydrates the interactive client with it,
    /// avoiding a second fetch and a loading flash.
    /// </para>
    /// </remarks>
    public static IDotNetQueryBuilder AddDotNetQueryBlazor(
        this IServiceCollection services,
        Action<QueryClientOptions>? configure = null)
    {
        var builder = services.AddDotNetQuery(ServiceLifetime.Scoped, configure);
        services.TryAddScoped<BrowserSignalService>();
        return builder;
    }

    /// <summary>
    /// Keeps the registered queries in sync across the browser tabs of the same origin: data fetched or written in
    /// one tab (including optimistic updates and their rollback) appears in the others, and invalidations reach every
    /// tab. Place <c>&lt;DotNetQueryCrossTabSync /&gt;</c> once in an interactive layout to start it.
    /// </summary>
    /// <param name="builder">The builder returned by <c>AddDotNetQueryBlazor</c>.</param>
    /// <param name="register">Registers the queries to synchronize, for example <c>sync =&gt; sync.Register(TodoQueries.All, AppJson.Default.ListTodo)</c>.</param>
    /// <param name="options">Synchronization options; set <see cref="QuerySyncOptions.ChannelName"/> to your app version so tabs of different builds do not mix.</param>
    /// <param name="channelName">The BroadcastChannel name.</param>
    /// <remarks>
    /// Messages carry serialized query data through the browser. In Blazor Server each tab's circuit has its own
    /// cache on the server, and the browser relays the data between them - only register data every tab of the
    /// signed-in user may see.
    /// </remarks>
    public static IDotNetQueryBuilder AddCrossTabSync(
        this IDotNetQueryBuilder builder,
        Action<QuerySynchronizer> register,
        QuerySyncOptions? options = null,
        string channelName = "dotnetquery")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(register);
        builder.Services.TryAddScoped(sp => new BroadcastChannelQuerySync(sp.GetRequiredService<IJSRuntime>(), channelName));
        builder.Services.TryAddScoped(sp =>
        {
            var synchronizer = new QuerySynchronizer(sp.GetRequiredService<IQueryClient>(), sp.GetRequiredService<BroadcastChannelQuerySync>(), options);
            register(synchronizer);
            return synchronizer.Start();
        });
        return builder;
    }

    /// <summary>
    /// Clears the query cache whenever the signed-in identity changes (sign-out, or sign-in as another user), so one
    /// user's cached data is never shown to the next - essential in Blazor WebAssembly, where the scoped client lives
    /// as long as the browser tab. Requires an <see cref="AuthenticationStateProvider"/> (the guard is inert
    /// without one). See <see cref="QueryAuthenticationGuard"/>.
    /// </summary>
    /// <param name="builder">The builder returned by <c>AddDotNetQueryBlazor</c>.</param>
    /// <param name="identity">
    /// Maps the principal to the identity whose change resets the cache (default
    /// <see cref="QueryAuthenticationGuard.DefaultIdentity"/>). Return the user plus a tenant claim to also reset on
    /// tenant switches.
    /// </param>
    public static IDotNetQueryBuilder ResetOnUserChange(this IDotNetQueryBuilder builder, Func<ClaimsPrincipal, string?>? identity = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAdd(ServiceDescriptor.Describe(
            typeof(QueryAuthenticationGuard),
            sp => new QueryAuthenticationGuard(sp.GetRequiredService<IQueryClient>(), sp.GetService<AuthenticationStateProvider>(), identity),
            builder.ClientLifetime));
        return builder;
    }

    /// <summary>
    /// Serializes the data <c>&lt;Query&gt;</c> and the hook-style queries carry from prerendering to the interactive
    /// render with source-generated metadata (for example <c>AppJson.Default</c>, a <c>JsonSerializerContext</c>), so
    /// prerender hydration is trim- and NativeAOT-safe. Types the resolver does not know fall back to reflection-based
    /// JSON. Register it in both the server and the WebAssembly project.
    /// </summary>
    public static IDotNetQueryBuilder UseHydrationJson(this IDotNetQueryBuilder builder, IJsonTypeInfoResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(resolver);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = resolver };
        options.MakeReadOnly();
        builder.Services.Replace(ServiceDescriptor.Singleton(new QueryHydrationJson(options)));
        return builder;
    }
}
