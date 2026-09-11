namespace HybridCache.Plus.Tenancy;

/// <summary>
/// High-performance, zero-allocation helper for extracting tenant identifiers from formatted cache keys.
/// </summary>
public static class TenantKeyParser
{
    /// <summary>
    /// Attempts to extract the tenant identifier from the beginning of a cache key using spans without heap allocations.
    /// Example: for key "tenants:tenant_123:orders:456" with prefix "tenants:", extracts "tenant_123".
    /// </summary>
    /// <param name="key">The complete cache key span.</param>
    /// <param name="prefixPattern">The prefix pattern indicating tenancy (e.g. "tenants:").</param>
    /// <param name="tenantId">The extracted tenant span if successful.</param>
    /// <returns>True if a non-empty tenant identifier was found; otherwise false.</returns>
    public static bool TryExtractTenantId(
        ReadOnlySpan<char> key,
        ReadOnlySpan<char> prefixPattern,
        out ReadOnlySpan<char> tenantId)
    {
        tenantId = default;

        if (key.IsEmpty || prefixPattern.IsEmpty)
        {
            return false;
        }

        if (!key.StartsWith(prefixPattern, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remainder = key.Slice(prefixPattern.Length);
        if (remainder.IsEmpty)
        {
            return false;
        }

        var nextSeparatorIndex = remainder.IndexOf(':');
        if (nextSeparatorIndex > 0)
        {
            tenantId = remainder.Slice(0, nextSeparatorIndex);
            return true;
        }

        if (nextSeparatorIndex < 0)
        {
            // The rest of the key is the tenant identifier
            tenantId = remainder;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Extracts the tenant identifier as a string, or null if the key does not match the tenancy pattern.
    /// </summary>
    public static string? ExtractTenantIdString(string key, string prefixPattern)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(prefixPattern))
        {
            return null;
        }

        return TryExtractTenantId(key.AsSpan(), prefixPattern.AsSpan(), out var tenantSpan)
            ? tenantSpan.ToString()
            : null;
    }
}
