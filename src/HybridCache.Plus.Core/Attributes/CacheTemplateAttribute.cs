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
    /// Enables fail-safe mode: every successful load also keeps a backup copy of the value for this many seconds,
    /// and when the factory throws (database down, network error...) that last known value is returned instead of the exception.
    /// If 0 or not specified, fail-safe is disabled for this method. Policies can tune or disable it at runtime.
    /// </summary>
    public int FailSafeMaxAgeSeconds { get; set; }

    /// <summary>
    /// Fail-safe only: when the factory takes longer than this many milliseconds and a backup value exists,
    /// the backup is returned immediately while the factory keeps running in the background to refresh the entry.
    /// If 0 or not specified, the caller always waits for the factory.
    /// </summary>
    public int FactorySoftTimeoutMs { get; set; }

    /// <summary>
    /// Fail-safe only: after the factory fails, how many seconds the backup value is served from the local (L1) cache
    /// before the factory is retried, so a struggling backend is not hammered by every request.
    /// If 0 or not specified, defaults to 30 seconds.
    /// </summary>
    public int FailSafeThrottleSeconds { get; set; }
}
