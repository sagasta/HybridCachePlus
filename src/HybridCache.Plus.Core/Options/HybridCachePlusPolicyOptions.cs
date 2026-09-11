namespace HybridCache.Plus.Options;

/// <summary>
/// Root configuration options for HybridCache.Plus cache policies and multi-tenant overrides.
/// </summary>
public sealed class HybridCachePlusPolicyOptions
{
    /// <summary>
    /// Default local (L1) cache expiration in seconds across all policies when not explicitly specified.
    /// </summary>
    public int? DefaultLocalTtlSeconds { get; set; }

    /// <summary>
    /// Default distributed (L2) cache expiration in seconds across all policies when not explicitly specified.
    /// </summary>
    public int? DefaultDistributedTtlSeconds { get; set; }

    /// <summary>
    /// Gets or sets whether OpenTelemetry / System.Diagnostics metrics and tracing are enabled.
    /// Default is true.
    /// </summary>
    public bool EnableDiagnostics { get; set; } = true;

    /// <summary>
    /// Global cache policies indexed by policy name or "{InterfaceName}.{MethodName}".
    /// </summary>
    public Dictionary<string, CachePolicyOptions> Policies { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Tenant-specific policy overrides indexed by tenant identifier.
    /// </summary>
    public Dictionary<string, TenantPolicyOptions> Tenants { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
