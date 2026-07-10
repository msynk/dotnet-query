using DotNetQuery;
using DotNetQuery.Blazor;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registration extensions for the DotNetQuery Blazor adapter.</summary>
public static class DotNetQueryBlazorServiceCollectionExtensions
{
    /// <summary>
    /// Registers DotNetQuery (via <see cref="DotNetQueryServiceCollectionExtensions.AddDotNetQuery"/>)
    /// together with the Blazor browser-signal bridge that forwards window focus/online/visibility
    /// events to the focus, online, resume, and connectivity managers.
    /// </summary>
    public static IDotNetQueryBuilder AddDotNetQueryBlazor(
        this IServiceCollection services,
        Action<QueryClientOptions>? configure = null)
    {
        var builder = services.AddDotNetQuery(configure);
        services.TryAddScoped<BrowserSignalService>();
        return builder;
    }
}
