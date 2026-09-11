namespace HybridCache.Plus.Generators.Models;

public sealed class ParameterModel(string name, string type, bool isCancellationToken = false)
    : IEquatable<ParameterModel>
{
    public string Name { get; } = name;
    public string Type { get; } = type;
    public bool IsCancellationToken { get; } = isCancellationToken;

    public bool Equals(ParameterModel? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Name == other.Name && Type == other.Type && IsCancellationToken == other.IsCancellationToken;
    }

    public override bool Equals(object? obj) => Equals(obj as ParameterModel);
    public override int GetHashCode() => (Name, Type, IsCancellationToken).GetHashCode();
}

public sealed class InvalidationTargetModel(
    string interfaceFullName,
    string interfaceName,
    string interfaceNamespace,
    string methodName,
    string keyTemplate,
    EquatableArray<string> tagTemplates)
    : IEquatable<InvalidationTargetModel>
{
    public string InterfaceFullName { get; } = interfaceFullName;
    public string InterfaceName { get; } = interfaceName;
    public string InterfaceNamespace { get; } = interfaceNamespace;
    public string MethodName { get; } = methodName;
    public string KeyTemplate { get; } = keyTemplate;
    public EquatableArray<string> TagTemplates { get; } = tagTemplates;

    public bool Equals(InvalidationTargetModel? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return InterfaceFullName == other.InterfaceFullName &&
               MethodName == other.MethodName &&
               KeyTemplate == other.KeyTemplate &&
               TagTemplates.Equals(other.TagTemplates);
    }

    public override bool Equals(object? obj) => Equals(obj as InvalidationTargetModel);
    public override int GetHashCode() => (InterfaceFullName, MethodName, KeyTemplate).GetHashCode();
}

public sealed class CacheMethodModel(
    string methodName,
    string returnType,
    bool isValueTask,
    string keyTemplate,
    string policyName,
    int localTtlSeconds,
    int distributedTtlSeconds,
    EquatableArray<string> tags,
    EquatableArray<ParameterModel> parameters,
    EquatableArray<InvalidationTargetModel> invalidationTargets)
    : IEquatable<CacheMethodModel>
{
    public string MethodName { get; } = methodName;
    public string ReturnType { get; } = returnType;
    public bool IsValueTask { get; } = isValueTask;
    public string KeyTemplate { get; } = keyTemplate;
    public string PolicyName { get; } = policyName;
    public int LocalTtlSeconds { get; } = localTtlSeconds;
    public int DistributedTtlSeconds { get; } = distributedTtlSeconds;
    public EquatableArray<string> Tags { get; } = tags;
    public EquatableArray<ParameterModel> Parameters { get; } = parameters;
    public EquatableArray<InvalidationTargetModel> InvalidationTargets { get; } = invalidationTargets;

    public bool Equals(CacheMethodModel? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return MethodName == other.MethodName &&
               ReturnType == other.ReturnType &&
               IsValueTask == other.IsValueTask &&
               KeyTemplate == other.KeyTemplate &&
               PolicyName == other.PolicyName &&
               LocalTtlSeconds == other.LocalTtlSeconds &&
               DistributedTtlSeconds == other.DistributedTtlSeconds &&
               Tags.Equals(other.Tags) &&
               Parameters.Equals(other.Parameters) &&
               InvalidationTargets.Equals(other.InvalidationTargets);
    }

    public override bool Equals(object? obj) => Equals(obj as CacheMethodModel);
    public override int GetHashCode() => (MethodName, KeyTemplate, PolicyName, LocalTtlSeconds, DistributedTtlSeconds).GetHashCode();
}

public sealed class CacheInterfaceModel(
    string interfaceNamespace,
    string interfaceName,
    EquatableArray<CacheMethodModel> methods)
    : IEquatable<CacheInterfaceModel>
{
    public string InterfaceNamespace { get; } = interfaceNamespace;
    public string InterfaceName { get; } = interfaceName;
    public EquatableArray<CacheMethodModel> Methods { get; } = methods;

    public bool Equals(CacheInterfaceModel? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return InterfaceNamespace == other.InterfaceNamespace &&
               InterfaceName == other.InterfaceName &&
               Methods.Equals(other.Methods);
    }

    public override bool Equals(object? obj) => Equals(obj as CacheInterfaceModel);
    public override int GetHashCode() => (InterfaceNamespace, InterfaceName).GetHashCode();
}
