using DotNetQuery;
using DotNetQuery.Blazor;
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
}
