namespace HybridCache.Plus.Generators.Models;

public sealed class EvictionActionModel(string keyTemplate, EquatableArray<string> tagTemplates)
    : IEquatable<EvictionActionModel>
{
    public string KeyTemplate { get; } = keyTemplate;
    public EquatableArray<string> TagTemplates { get; } = tagTemplates;

    public bool Equals(EvictionActionModel? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return KeyTemplate == other.KeyTemplate && TagTemplates.Equals(other.TagTemplates);
    }

    public override bool Equals(object? obj) => Equals(obj as EvictionActionModel);
    public override int GetHashCode() => (KeyTemplate, TagTemplates).GetHashCode();
}

public sealed class DecoratorMethodModel(
    string signatureId,
    string methodName,
    string returnType,
    bool returnsVoid,
    bool isAsync,
    bool isGenericTask,
    string? genericReturnType,
    EquatableArray<ParameterModel> parameters,
    EquatableArray<EvictionActionModel> evictionActions)
    : IEquatable<DecoratorMethodModel>
{
    /// <summary>
    /// Uniquely identifies the method (including overloads) inside its interface.
    /// </summary>
    public string SignatureId { get; } = signatureId;
    public string MethodName { get; } = methodName;
    public string ReturnType { get; } = returnType;
    public bool ReturnsVoid { get; } = returnsVoid;
    public bool IsAsync { get; } = isAsync;
    public bool IsGenericTask { get; } = isGenericTask;
    public string? GenericReturnType { get; } = genericReturnType;
    public EquatableArray<ParameterModel> Parameters { get; } = parameters;
    public EquatableArray<EvictionActionModel> EvictionActions { get; } = evictionActions;

    public bool Equals(DecoratorMethodModel? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return SignatureId == other.SignatureId &&
               MethodName == other.MethodName &&
               ReturnType == other.ReturnType &&
               ReturnsVoid == other.ReturnsVoid &&
               IsAsync == other.IsAsync &&
               IsGenericTask == other.IsGenericTask &&
               GenericReturnType == other.GenericReturnType &&
               Parameters.Equals(other.Parameters) &&
               EvictionActions.Equals(other.EvictionActions);
    }

    public override bool Equals(object? obj) => Equals(obj as DecoratorMethodModel);
    public override int GetHashCode() => (MethodName, ReturnType, IsAsync).GetHashCode();
}

public sealed class DecoratorInterfaceModel(
    string interfaceNamespace,
    string interfaceName,
    string interfaceFullName,
    string decoratorClassName,
    EquatableArray<DecoratorMethodModel> methods)
    : IEquatable<DecoratorInterfaceModel>
{
    public string InterfaceNamespace { get; } = interfaceNamespace;
    public string InterfaceName { get; } = interfaceName;
    public string InterfaceFullName { get; } = interfaceFullName;
    public string DecoratorClassName { get; } = decoratorClassName;
    public EquatableArray<DecoratorMethodModel> Methods { get; } = methods;

    public bool Equals(DecoratorInterfaceModel? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return InterfaceNamespace == other.InterfaceNamespace &&
               InterfaceName == other.InterfaceName &&
               InterfaceFullName == other.InterfaceFullName &&
               DecoratorClassName == other.DecoratorClassName &&
               Methods.Equals(other.Methods);
    }

    public override bool Equals(object? obj) => Equals(obj as DecoratorInterfaceModel);
    public override int GetHashCode() => (InterfaceNamespace, InterfaceName, DecoratorClassName).GetHashCode();
}

/// <summary>
/// An eviction contributed by a cache contract to a method of a mutating (decorated) interface.
/// </summary>
public sealed class PendingEvictionModel(string interfaceFullName, string signatureId, EvictionActionModel action)
    : IEquatable<PendingEvictionModel>
{
    public string InterfaceFullName { get; } = interfaceFullName;
    public string SignatureId { get; } = signatureId;
    public EvictionActionModel Action { get; } = action;

    public bool Equals(PendingEvictionModel? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return InterfaceFullName == other.InterfaceFullName &&
               SignatureId == other.SignatureId &&
               Action.Equals(other.Action);
    }

    public override bool Equals(object? obj) => Equals(obj as PendingEvictionModel);
    public override int GetHashCode() => (InterfaceFullName, SignatureId, Action).GetHashCode();
}

/// <summary>
/// Symbol-free, value-equatable output of the per-interface transform, so the incremental pipeline can cache it.
/// </summary>
public sealed class InterfaceExtractionResult(
    CacheInterfaceModel? interfaceModel,
    EquatableArray<DiagnosticInfo> diagnostics,
    EquatableArray<DecoratorInterfaceModel> decoratorTargets,
    EquatableArray<PendingEvictionModel> pendingEvictions)
    : IEquatable<InterfaceExtractionResult>
{
    public CacheInterfaceModel? InterfaceModel { get; } = interfaceModel;
    public EquatableArray<DiagnosticInfo> Diagnostics { get; } = diagnostics;

    /// <summary>
    /// Shapes of the mutating interfaces referenced by [InvalidatedBy], including their own [InvalidatesTag] evictions.
    /// </summary>
    public EquatableArray<DecoratorInterfaceModel> DecoratorTargets { get; } = decoratorTargets;

    /// <summary>
    /// Evictions this cache contract adds to methods of those mutating interfaces.
    /// </summary>
    public EquatableArray<PendingEvictionModel> PendingEvictions { get; } = pendingEvictions;

    public bool Equals(InterfaceExtractionResult? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Equals(InterfaceModel, other.InterfaceModel) &&
               Diagnostics.Equals(other.Diagnostics) &&
               DecoratorTargets.Equals(other.DecoratorTargets) &&
               PendingEvictions.Equals(other.PendingEvictions);
    }

    public override bool Equals(object? obj) => Equals(obj as InterfaceExtractionResult);
    public override int GetHashCode() => (InterfaceModel?.GetHashCode() ?? 0, Diagnostics.Count, PendingEvictions.Count).GetHashCode();
}
