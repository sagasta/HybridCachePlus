using Microsoft.CodeAnalysis;

namespace HybridCache.Plus.Generators.Diagnostics;

public static class DiagnosticDescriptors
{
    private const string Category = "HybridCache.Plus";

    public static readonly DiagnosticDescriptor MissingTemplateParameter = new(
        id: "HCP001",
        title: "Cache template placeholder not found in method parameters",
        messageFormat: "Cache template placeholder '{{{0}}}' in method '{1}' does not match any parameter in the method signature",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "All placeholders defined in a [CacheTemplate] or tag template must correspond to a method parameter with the same name (case-insensitive).");

    public static readonly DiagnosticDescriptor InvalidatedByTargetNotFound = new(
        id: "HCP002",
        title: "InvalidatedBy target method or interface not found",
        messageFormat: "Target method '{0}' was not found on interface '{1}' specified in [InvalidatedBy]",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The [InvalidatedBy] attribute must reference a valid method on an interface.");

    public static readonly DiagnosticDescriptor InvalidReturnType = new(
        id: "HCP003",
        title: "Invalid return type for cache method",
        messageFormat: "Cache contract method '{0}' must return ValueTask<T> or Task<T>",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Methods decorated with [CacheTemplate] must return ValueTask<T> or Task<T>.");

    public static readonly DiagnosticDescriptor TenantPlaceholderMissingInTemplate = new(
        id: "HCP004",
        title: "Multi-tenant cache template must include {tenantId} placeholder",
        messageFormat: "Method '{0}' has a tenant parameter '{1}', but the cache template '{2}' does not include '{{{1}}}', risking cross-tenant L1 cache collisions",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Multi-tenant methods with a tenant parameter must include the tenant placeholder in their [CacheTemplate] to guarantee L1 cache isolation between tenants.");
}
