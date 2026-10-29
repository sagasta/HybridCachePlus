using HybridCache.Plus;
using HybridCache.Plus.Diagnostics;
using HybridCache.Plus.Options;
using HybridCache.Plus.Policies;
using HybridCache.Plus.Tenancy;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Dependency injection registration extensions for HybridCache.Plus.
/// </summary>
public static class HybridCachePlusServiceCollectionExtensions
{
    /// <summary>
    /// Registers HybridCache.Plus core services, policy registry, and returns a builder to configure extensions.
    /// </summary>
    public static IServiceCollection AddHybridCachePlus(
        this IServiceCollection services,
        Action<HybridCachePlusBuilder>? configure = null)
    {
        services.AddOptions<HybridCachePlusPolicyOptions>();

        var builder = new HybridCachePlusBuilder(services);
        configure?.Invoke(builder);

        services.TryAddSingleton<HybridCachePlusPolicyRegistry>(sp =>
        {
            var monitor = sp.GetRequiredService<IOptionsMonitor<HybridCachePlusPolicyOptions>>();
            var options = monitor.CurrentValue;
            HybridCachePlusDiagnostics.IsEnabled = options.EnableDiagnostics;

            var tenantAccessor = sp.GetService<ITenantContextAccessor>();
            var registry = new HybridCachePlusPolicyRegistry(options, tenantAccessor);

            // Hot reload: options bound to configuration (e.g. appsettings.json) are re-applied without restarting
            monitor.OnChange(updated =>
            {
                HybridCachePlusDiagnostics.IsEnabled = updated.EnableDiagnostics;
                registry.Reload(updated);
            });

            // Generated code resolves policies per HybridCache instance; Current/ambient accessor remain as fallbacks
            var cache = sp.GetService<global::Microsoft.Extensions.Caching.Hybrid.HybridCache>();
            if (cache != null)
            {
                HybridCachePlusPolicyRegistry.Register(cache, registry);
            }
            HybridCachePlusPolicyRegistry.SetAmbientTenantAccessor(tenantAccessor);
            HybridCachePlusPolicyRegistry.Current = registry;
            return registry;
        });

        // Activates the registry on host start, so configuration-bound policies apply before the first cache call
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, HybridCachePlusPolicyActivator>());

        return services;
    }

    private sealed class HybridCachePlusPolicyActivator(HybridCachePlusPolicyRegistry registry) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            GC.KeepAlive(registry);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
