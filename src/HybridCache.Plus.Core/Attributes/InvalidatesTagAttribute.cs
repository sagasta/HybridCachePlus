namespace HybridCache.Plus;

/// <summary>
/// Declares that execution of a target interface method or manual call invalidates a specific tag template.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
public sealed class InvalidatesTagAttribute(string tagTemplate) : Attribute
{
    /// <summary>
    /// The tag template to invalidate (e.g. "tenant:{tenantId}").
    /// </summary>
    public string TagTemplate { get; } = tagTemplate ?? throw new ArgumentNullException(nameof(tagTemplate));
}
