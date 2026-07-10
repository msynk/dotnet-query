using DotNetQuery;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registration extensions for wiring DotNetQuery into a <see cref="IServiceCollection"/>.
/// </summary>
public static class DotNetQueryServiceCollectionExtensions
{
    /// <summary>
    /// Registers a singleton <see cref="IQueryClient"/> and its default options.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional callback to customize the client-wide defaults.</param>
    /// <returns>A <see cref="IDotNetQueryBuilder"/> for further configuration.</returns>
    public static IDotNetQueryBuilder AddDotNetQuery(
        this IServiceCollection services,
        Action<QueryClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new QueryClientOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);

        // Signal managers are adapter-driven; register defaults so adapters can inject and raise them.
        services.TryAddSingleton<FocusManager>();
        services.TryAddSingleton<OnlineManager>();
        services.TryAddSingleton<ResumeManager>();
        services.TryAddSingleton<IFocusManager>(sp => sp.GetRequiredService<FocusManager>());
        services.TryAddSingleton<IOnlineManager>(sp => sp.GetRequiredService<OnlineManager>());
        services.TryAddSingleton<IResumeManager>(sp => sp.GetRequiredService<ResumeManager>());

        // Connectivity for offline pause/resume; adapters update it from native network APIs.
        services.TryAddSingleton<ConnectivityProvider>();
        services.TryAddSingleton<IConnectivityProvider>(sp => sp.GetRequiredService<ConnectivityProvider>());

        services.TryAddSingleton<IQueryClient>(sp => new QueryClient(
            sp.GetRequiredService<QueryClientOptions>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IFocusManager>(),
            sp.GetRequiredService<IOnlineManager>(),
            sp.GetRequiredService<IResumeManager>(),
            sp.GetRequiredService<IConnectivityProvider>()));

        return new DotNetQueryBuilder(services);
    }
}

/// <summary>
/// Fluent builder returned by <see cref="DotNetQueryServiceCollectionExtensions.AddDotNetQuery"/>,
/// enabling adapters and extensions to attach their own registrations.
/// </summary>
public interface IDotNetQueryBuilder
{
    /// <summary>The underlying service collection.</summary>
    IServiceCollection Services { get; }
}

internal sealed class DotNetQueryBuilder(IServiceCollection services) : IDotNetQueryBuilder
{
    public IServiceCollection Services { get; } = services;
}
