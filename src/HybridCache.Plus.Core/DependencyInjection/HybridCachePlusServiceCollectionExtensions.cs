using HybridCache.Plus;
using HybridCache.Plus.Options;
using HybridCache.Plus.Policies;
using HybridCache.Plus.Tenancy;
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

        services.AddSingleton<HybridCachePlusPolicyRegistry>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<HybridCachePlusPolicyOptions>>().Value;
            HybridCache.Plus.Diagnostics.HybridCachePlusDiagnostics.IsEnabled = options.EnableDiagnostics;
            var registry = new HybridCachePlusPolicyRegistry(options);
            var tenantAccessor = sp.GetService<ITenantContextAccessor>();
            HybridCachePlusPolicyRegistry.SetAmbientTenantAccessor(tenantAccessor);
            HybridCachePlusPolicyRegistry.Current = registry;
            return registry;
        });

        return services;
    }
}
