using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using HybridCache.Plus.Options;
using HybridCache.Plus.Policies;
using HybridCache.Plus.Tenancy;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Fluent configuration builder for HybridCache.Plus features.
/// </summary>
public sealed class HybridCachePlusBuilder
{
    private readonly HybridCachePlusPolicyOptions _policyOptions = new();

    /// <summary>
    /// The application service collection.
    /// </summary>
    public IServiceCollection Services { get; }

    public HybridCachePlusBuilder(IServiceCollection services)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>
    /// Configures global, method, and multi-tenant cache policies and TTLs.
    /// </summary>
    public HybridCachePlusBuilder ConfigurePolicies(Action<HybridCachePlusPolicyOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_policyOptions);
        Services.Configure(configure);
        HybridCachePlusPolicyRegistry.Current = new HybridCachePlusPolicyRegistry(_policyOptions);
        return this;
    }

    /// <summary>
    /// Configures a specific named policy or contract method TTL.
    /// </summary>
    public HybridCachePlusBuilder ConfigurePolicy(string policyName, Action<CachePolicyOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(policyName);
        ArgumentNullException.ThrowIfNull(configure);

        if (!_policyOptions.Policies.TryGetValue(policyName, out var policy))
        {
            policy = new CachePolicyOptions();
            _policyOptions.Policies[policyName] = policy;
        }
        configure(policy);

        Services.Configure<HybridCachePlusPolicyOptions>(options =>
        {
            if (!options.Policies.TryGetValue(policyName, out var p))
            {
                p = new CachePolicyOptions();
                options.Policies[policyName] = p;
            }
            configure(p);
        });

        HybridCachePlusPolicyRegistry.Current = new HybridCachePlusPolicyRegistry(_policyOptions);
        return this;
    }

    /// <summary>
    /// Configures a tenant-specific cache policy override.
    /// </summary>
    public HybridCachePlusBuilder ConfigureTenantPolicy(string tenantId, string policyName, Action<CachePolicyOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(policyName);
        ArgumentNullException.ThrowIfNull(configure);

        if (!_policyOptions.Tenants.TryGetValue(tenantId, out var tenant))
        {
            tenant = new TenantPolicyOptions();
            _policyOptions.Tenants[tenantId] = tenant;
        }
        if (!tenant.Policies.TryGetValue(policyName, out var policy))
        {
            policy = new CachePolicyOptions();
            tenant.Policies[policyName] = policy;
        }
        configure(policy);

        Services.Configure<HybridCachePlusPolicyOptions>(options =>
        {
            if (!options.Tenants.TryGetValue(tenantId, out var t))
            {
                t = new TenantPolicyOptions();
                options.Tenants[tenantId] = t;
            }
            if (!t.Policies.TryGetValue(policyName, out var p))
            {
                p = new CachePolicyOptions();
                t.Policies[policyName] = p;
            }
            configure(p);
        });

        HybridCachePlusPolicyRegistry.Current = new HybridCachePlusPolicyRegistry(_policyOptions);
        return this;
    }
}

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
            global::HybridCache.Plus.Diagnostics.HybridCachePlusDiagnostics.IsEnabled = options.EnableDiagnostics;
            var registry = new HybridCachePlusPolicyRegistry(options);
            var tenantAccessor = sp.GetService<ITenantContextAccessor>();
            HybridCachePlusPolicyRegistry.SetAmbientTenantAccessor(tenantAccessor);
            HybridCachePlusPolicyRegistry.Current = registry;
            return registry;
        });

        return services;
    }
}
