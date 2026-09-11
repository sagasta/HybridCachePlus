namespace HybridCache.Plus;

/// <summary>
/// Defines the cache key template, TTL policies, tags, and entry configuration for a cached contract method.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class CacheTemplateAttribute(string template) : Attribute
{
    /// <summary>
    /// Gets the parameterized key template (e.g. "tenants:{tenantId}:products:{productId}").
    /// </summary>
    public string Template { get; } = template ?? throw new ArgumentNullException(nameof(template));

    /// <summary>
    /// Gets or sets the policy name for external configuration mapping (e.g. "CatalogProducts").
    /// If null or not specified, defaults to the enclosing interface and method name ("{InterfaceName}.{MethodName}").
    /// </summary>
    public string? PolicyName { get; set; }

    /// <summary>
    /// Gets or sets the local (L1 in-memory) cache expiration time in seconds.
    /// If 0 or not specified, defaults to the underlying HybridCache default policy.
    /// </summary>
    public int LocalTtlSeconds { get; set; }

    /// <summary>
    /// Gets or sets the distributed (L2 Redis / Garnet) cache expiration time in seconds.
    /// If 0 or not specified, defaults to the underlying HybridCache default policy.
    /// </summary>
    public int DistributedTtlSeconds { get; set; }

    /// <summary>
    /// Gets or sets the collection of tag templates associated with this cache entry (e.g. ["tenant:{tenantId}"]).
    /// </summary>
    public string[] Tags { get; set; } = [];

    /// <summary>
    /// Gets or sets whether defensive copies should be made for mutable cache entries.
    /// </summary>
    public bool DefensiveCopy { get; set; }
}
