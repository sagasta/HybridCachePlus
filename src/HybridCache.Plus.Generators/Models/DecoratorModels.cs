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
        return MethodName == other.MethodName &&
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
