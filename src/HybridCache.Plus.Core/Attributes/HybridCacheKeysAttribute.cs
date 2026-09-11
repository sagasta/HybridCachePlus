namespace HybridCache.Plus;

/// <summary>
/// Marks an interface as a typed HybridCache contract for compile-time code generation.
/// </summary>
[AttributeUsage(AttributeTargets.Interface, Inherited = false, AllowMultiple = false)]
public sealed class HybridCacheKeysAttribute : Attribute;
