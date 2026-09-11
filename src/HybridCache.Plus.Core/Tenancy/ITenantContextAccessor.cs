namespace HybridCache.Plus.Tenancy;

/// <summary>
/// Provides access to the current tenant identifier for multi-tenant caching operations.
/// </summary>
public interface ITenantContextAccessor
{
    /// <summary>
    /// Gets or sets the current tenant identifier for the ambient asynchronous execution flow.
    /// </summary>
    string? CurrentTenantId { get; set; }
}

/// <summary>
/// Default implementation of <see cref="ITenantContextAccessor"/> using <see cref="AsyncLocal{T}"/>.
/// </summary>
public sealed class AsyncLocalTenantContextAccessor : ITenantContextAccessor
{
    private static readonly AsyncLocal<string?> _currentTenantId = new();

    /// <inheritdoc />
    public string? CurrentTenantId
    {
        get => _currentTenantId.Value;
        set => _currentTenantId.Value = value;
    }
}
