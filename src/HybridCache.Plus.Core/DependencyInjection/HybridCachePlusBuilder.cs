using HybridCache.Plus.Options;
using HybridCache.Plus.Policies;
using Microsoft.Extensions.DependencyInjection;

namespace HybridCache.Plus;

/// <summary>
/// Fluent configuration builder for HybridCache.Plus features.
/// </summary>
public sealed class HybridCachePlusBuilder(IServiceCollection services)
{
    private readonly HybridCachePlusPolicyOptions _policyOptions = new();

    /// <summary>
    /// The application service collection.
    /// </summary>
    public IServiceCollection Services { get; } = services ?? throw new ArgumentNullException(nameof(services));

    /// <summary>
    /// Configures whether OpenTelemetry metrics and tracing instrumentation are enabled.
    /// </summary>
    public HybridCachePlusBuilder EnableDiagnostics(bool enabled = true)
    {
        _policyOptions.EnableDiagnostics = enabled;
        Services.Configure<HybridCachePlusPolicyOptions>(options => options.EnableDiagnostics = enabled);
        HybridCache.Plus.Diagnostics.HybridCachePlusDiagnostics.IsEnabled = enabled;
        return this;
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
