namespace HybridCache.Plus;

/// <summary>
/// Declares that execution of one or more target interface methods (e.g. repository mutations)
/// will automatically trigger invalidation of this cached entry.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
public class InvalidatedByAttribute(Type targetInterface, params string[] methodNames) : Attribute
{
    /// <summary>
    /// The target interface type containing the mutating method(s).
    /// </summary>
    public Type TargetInterface { get; } = targetInterface ?? throw new ArgumentNullException(nameof(targetInterface));

    /// <summary>
    /// The name(s) of the mutating method(s) on the target interface.
    /// </summary>
    public string[] MethodNames { get; } = methodNames ?? throw new ArgumentNullException(nameof(methodNames));

    public InvalidatedByAttribute(Type targetInterface, string methodName)
        : this(targetInterface, [methodName])
    {
    }
}

/// <summary>
/// Strongly-typed generic variant of <see cref="InvalidatedByAttribute"/> for compile-time safety and IDE refactoring.
/// </summary>
/// <typeparam name="TTarget">The target interface containing the mutating method(s).</typeparam>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
public sealed class InvalidatedByAttribute<TTarget> : InvalidatedByAttribute
    where TTarget : class
{
    public InvalidatedByAttribute(params string[] methodNames)
        : base(typeof(TTarget), methodNames)
    {
    }

    public InvalidatedByAttribute(string methodName)
        : base(typeof(TTarget), methodName)
    {
    }
}
