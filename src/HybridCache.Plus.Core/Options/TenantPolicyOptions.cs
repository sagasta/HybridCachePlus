namespace HybridCache.Plus.Options;

/// <summary>
/// Cache configuration policies specific to a tenant.
/// </summary>
public sealed class TenantPolicyOptions
{
    /// <summary>
    /// Method and contract-specific policy overrides for this tenant.
    /// </summary>
    public Dictionary<string, CachePolicyOptions> Policies { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Default local (L1) expiration time in seconds for this tenant if not overridden per policy.
    /// </summary>
    public int? DefaultLocalTtlSeconds { get; set; }

    /// <summary>
    /// Default distributed (L2) expiration time in seconds for this tenant if not overridden per policy.
    /// </summary>
    public int? DefaultDistributedTtlSeconds { get; set; }
}
