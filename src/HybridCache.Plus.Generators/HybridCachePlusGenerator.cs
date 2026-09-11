using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using HybridCache.Plus.Generators.Diagnostics;
using HybridCache.Plus.Generators.Emitters;
using HybridCache.Plus.Generators.Models;
using HybridCache.Plus.Generators.Parsers;

namespace HybridCache.Plus.Generators;

[Generator(LanguageNames.CSharp)]
public sealed class HybridCachePlusGenerator : IIncrementalGenerator
{
    private const string HybridCacheKeysAttributeName = "HybridCache.Plus.HybridCacheKeysAttribute";
    private const string HybridCacheKeysAttributeNameAlt = "HybridCache.Plus.Attributes.HybridCacheKeysAttribute";
    private const string CacheTemplateAttributeName = "HybridCache.Plus.CacheTemplateAttribute";
    private const string InvalidatedByAttributeName = "HybridCache.Plus.InvalidatedByAttribute";
    private const string InvalidatesTagAttributeName = "HybridCache.Plus.InvalidatesTagAttribute";

    private static readonly SymbolDisplayFormat NullableFullyQualifiedFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
                              SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
                              SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // 1. Discover all interfaces annotated with [HybridCacheKeys] (supports both HybridCache.Plus and HybridCache.Plus.Attributes)
        var interfaceResults1 = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                HybridCacheKeysAttributeName,
                predicate: static (node, _) => node is InterfaceDeclarationSyntax,
                transform: static (ctx, _) => ExtractInterface(ctx));

        var interfaceResults2 = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                HybridCacheKeysAttributeNameAlt,
                predicate: static (node, _) => node is InterfaceDeclarationSyntax,
                transform: static (ctx, _) => ExtractInterface(ctx));

        var allResults = interfaceResults1.Collect().Combine(interfaceResults2.Collect()).Select(static (pair, _) =>
        {
            var seen = new HashSet<string>();
            var builder = ImmutableArray.CreateBuilder<InterfaceExtractionResult>();
            foreach (var r in pair.Left.Concat(pair.Right))
            {
                var key = r.InterfaceModel != null
                    ? $"{r.InterfaceModel.InterfaceNamespace}.{r.InterfaceModel.InterfaceName}"
                    : r.GetHashCode().ToString();
                if (seen.Add(key))
                {
                    builder.Add(r);
                }
            }
            return builder.ToImmutable();
        });

        // 2. Output diagnostics, extension methods, and decorators
        context.RegisterSourceOutput(allResults, static (spc, results) =>
        {
            foreach (var result in results)
            {
                foreach (var diag in result.Diagnostics)
                {
                    spc.ReportDiagnostic(diag.ToDiagnostic());
                }

                if (result.InterfaceModel != null)
                {
                    var source = ExtensionMethodsEmitter.Emit(result.InterfaceModel);
                    var hintName = $"{result.InterfaceModel.InterfaceName}HybridCacheExtensions.g.cs";
                    spc.AddSource(hintName, SourceText.From(source, Encoding.UTF8));
                }
            }

            // Collect all invalidation targets
            var invalidationsByInterface = new Dictionary<string, List<(InvalidationTargetModel Target, INamedTypeSymbol InterfaceSymbol)>>();

            foreach (var result in results)
            {
                foreach (var item in result.PendingInvalidations)
                {
                    if (!invalidationsByInterface.TryGetValue(item.Target.InterfaceFullName, out var list))
                    {
                        list = [];
                        invalidationsByInterface[item.Target.InterfaceFullName] = list;
                    }
                    list.Add(item);
                }
            }

            // Generate decorator for each target interface
            foreach (var kvp in invalidationsByInterface)
            {
                var targetList = kvp.Value;
                if (targetList.Count == 0) continue;

                var targetInterfaceSymbol = targetList[0].InterfaceSymbol;
                var decoratorModel = BuildDecoratorModel(targetInterfaceSymbol, [.. targetList.Select(t => t.Target)]);

                if (decoratorModel != null)
                {
                    // Emit Decorator Class
                    var decoratorSource = DecoratorEmitter.Emit(decoratorModel);
                    var decoratorHintName = $"{decoratorModel.DecoratorClassName}.g.cs";
                    spc.AddSource(decoratorHintName, SourceText.From(decoratorSource, Encoding.UTF8));

                    // Emit DI Registration
                    var diSource = DiRegistrationEmitter.Emit(decoratorModel);
                    var diHintName = $"{decoratorModel.DecoratorClassName}Extensions.g.cs";
                    spc.AddSource(diHintName, SourceText.From(diSource, Encoding.UTF8));
                }
            }
        });
    }

    private static InterfaceExtractionResult ExtractInterface(GeneratorAttributeSyntaxContext ctx)
    {
        var diagnostics = new List<DiagnosticInfo>();
        var pendingInvalidations = new List<(InvalidationTargetModel Target, INamedTypeSymbol InterfaceSymbol)>();

        if (ctx.TargetSymbol is not INamedTypeSymbol interfaceSymbol || interfaceSymbol.TypeKind != TypeKind.Interface)
        {
            return new InterfaceExtractionResult(null, EquatableArray<DiagnosticInfo>.Empty, EquatableArray<(InvalidationTargetModel, INamedTypeSymbol)>.Empty);
        }

        var ns = interfaceSymbol.ContainingNamespace.IsGlobalNamespace
            ? ""
            : interfaceSymbol.ContainingNamespace.ToDisplayString();
        var interfaceName = interfaceSymbol.Name;

        var methods = new List<CacheMethodModel>();

        foreach (var member in interfaceSymbol.GetMembers())
        {
            if (member is not IMethodSymbol methodSymbol) continue;

            var cacheTemplateAttr = methodSymbol.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass?.ToDisplayString() == CacheTemplateAttributeName ||
                a.AttributeClass?.Name == "CacheTemplateAttribute" ||
                a.AttributeClass?.Name == "CacheTemplate");

            if (cacheTemplateAttr == null) continue;

            // Extract Template
            var template = cacheTemplateAttr.ConstructorArguments.Length > 0
                ? cacheTemplateAttr.ConstructorArguments[0].Value as string
                : null;

            template ??= "";

            var localTtl = 0;
            var distributedTtl = 0;
            var tagsList = new List<string>();
            string? policyName = null;

            foreach (var namedArg in cacheTemplateAttr.NamedArguments)
            {
                if (namedArg.Key == "PolicyName" && namedArg.Value.Value is string pn && !string.IsNullOrWhiteSpace(pn))
                {
                    policyName = pn;
                }
                else if (namedArg.Key == "LocalTtlSeconds" && namedArg.Value.Value is int lt)
                {
                    localTtl = lt;
                }
                else if (namedArg.Key == "DistributedTtlSeconds" && namedArg.Value.Value is int dt)
                {
                    distributedTtl = dt;
                }
                else if (namedArg.Key == "Tags" && namedArg.Value.Values != null)
                {
                    foreach (var tagConstant in namedArg.Value.Values)
                    {
                        if (tagConstant.Value is string s && !string.IsNullOrWhiteSpace(s))
                        {
                            tagsList.Add(s);
                        }
                    }
                }
            }

            // Also include any tag templates declared via [InvalidatesTag] on the cache method
            var additionalTagAttrs = methodSymbol.GetAttributes().Where(a =>
                a.AttributeClass != null &&
                (a.AttributeClass.Name == "InvalidatesTagAttribute" ||
                 a.AttributeClass.Name == "InvalidatesTag"));

            foreach (var tagAttr in additionalTagAttrs)
            {
                if (tagAttr.ConstructorArguments.Length > 0 &&
                    tagAttr.ConstructorArguments[0].Value is string tag &&
                    !string.IsNullOrWhiteSpace(tag) &&
                    !tagsList.Contains(tag))
                {
                    tagsList.Add(tag);
                }
            }

            policyName ??= $"{interfaceName}.{methodSymbol.Name}";

            // Validate Return Type (must be ValueTask<T> or Task<T>)
            var isValueTask = false;
            string returnTypeStr;

            if (methodSymbol.ReturnType is INamedTypeSymbol { IsGenericType: true } returnType &&
                (returnType.Name == "ValueTask" || returnType.Name == "Task"))
            {
                isValueTask = returnType.Name == "ValueTask";
                var typeArg = returnType.TypeArguments[0];
                returnTypeStr = typeArg.ToDisplayString(NullableFullyQualifiedFormat);
            }
            else
            {
                diagnostics.Add(new DiagnosticInfo(
                    DiagnosticDescriptors.InvalidReturnType,
                    methodSymbol.Locations.FirstOrDefault(),
                    methodSymbol.Name));
                returnTypeStr = "object";
            }

            // Extract Parameters
            var parameters = new List<ParameterModel>();
            var paramNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var p in methodSymbol.Parameters)
            {
                var isCt = p.Type.ToDisplayString().Contains("CancellationToken");
                parameters.Add(new ParameterModel(
                    p.Name,
                    p.Type.ToDisplayString(NullableFullyQualifiedFormat),
                    isCt));
                paramNames.Add(p.Name);
            }

            // Validate Template Placeholders against Method Parameters (HCP001)
            var templatePlaceholders = TemplateParser.ExtractPlaceholders(template);
            foreach (var placeholder in templatePlaceholders)
            {
                if (!paramNames.Contains(placeholder))
                {
                    diagnostics.Add(new DiagnosticInfo(
                        DiagnosticDescriptors.MissingTemplateParameter,
                        methodSymbol.Locations.FirstOrDefault(),
                        placeholder,
                        methodSymbol.Name));
                }
            }

            // Validate Tag Placeholders against Method Parameters (HCP001)
            foreach (var tag in tagsList)
            {
                var tagPlaceholders = TemplateParser.ExtractPlaceholders(tag);
                foreach (var placeholder in tagPlaceholders)
                {
                    if (!paramNames.Contains(placeholder))
                    {
                        diagnostics.Add(new DiagnosticInfo(
                            DiagnosticDescriptors.MissingTemplateParameter,
                            methodSymbol.Locations.FirstOrDefault(),
                            placeholder,
                            methodSymbol.Name));
                    }
                }
            }

            // Validate Multi-Tenant Placeholder Isolation (HCP004)
            var tenantParam = methodSymbol.Parameters.FirstOrDefault(p =>
                string.Equals(p.Name, "tenantId", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.Name, "tenant", StringComparison.OrdinalIgnoreCase));

            if (tenantParam != null)
            {
                var hasTenantPlaceholder = templatePlaceholders.Any(ph =>
                    string.Equals(ph, tenantParam.Name, StringComparison.OrdinalIgnoreCase));

                if (!hasTenantPlaceholder)
                {
                    diagnostics.Add(new DiagnosticInfo(
                        DiagnosticDescriptors.TenantPlaceholderMissingInTemplate,
                        methodSymbol.Locations.FirstOrDefault(),
                        methodSymbol.Name,
                        tenantParam.Name,
                        template));
                }
            }

            // Extract InvalidatedBy Attributes (both generic [InvalidatedBy<T>] and non-generic [InvalidatedBy(typeof(T))])
            var invalidationTargets = new List<InvalidationTargetModel>();
            var invalidatedByAttrs = methodSymbol.GetAttributes().Where(a =>
                a.AttributeClass != null &&
                (a.AttributeClass.Name == "InvalidatedByAttribute" ||
                 a.AttributeClass.Name == "InvalidatedBy"));

            foreach (var invAttr in invalidatedByAttrs)
            {
                INamedTypeSymbol? targetTypeSymbol = null;
                var methodArgsStartIndex = 0;

                if (invAttr.AttributeClass!.IsGenericType && invAttr.AttributeClass.TypeArguments.Length > 0)
                {
                    targetTypeSymbol = invAttr.AttributeClass.TypeArguments[0] as INamedTypeSymbol;
                    methodArgsStartIndex = 0;
                }
                else if (invAttr.ConstructorArguments.Length > 0 &&
                         invAttr.ConstructorArguments[0].Value is INamedTypeSymbol nonGenericTarget)
                {
                    targetTypeSymbol = nonGenericTarget;
                    methodArgsStartIndex = 1;
                }

                if (targetTypeSymbol == null) continue;

                // Extract all method names from params or multiple string arguments
                var methodNames = new List<string>();
                for (var argIdx = methodArgsStartIndex; argIdx < invAttr.ConstructorArguments.Length; argIdx++)
                {
                    var arg = invAttr.ConstructorArguments[argIdx];
                    if (arg.Kind == TypedConstantKind.Array)
                    {
                        foreach (var val in arg.Values)
                        {
                            if (val.Value is string s && !string.IsNullOrWhiteSpace(s))
                            {
                                methodNames.Add(s);
                            }
                        }
                    }
                    else if (arg.Value is string s && !string.IsNullOrWhiteSpace(s))
                    {
                        methodNames.Add(s);
                    }
                }

                var isInterface = targetTypeSymbol.TypeKind == TypeKind.Interface;

                foreach (var targetMethodName in methodNames)
                {
                    var targetMethod = targetTypeSymbol.GetMembers(targetMethodName).OfType<IMethodSymbol>().FirstOrDefault();

                    if (!isInterface || targetMethod == null)
                    {
                        diagnostics.Add(new DiagnosticInfo(
                            DiagnosticDescriptors.InvalidatedByTargetNotFound,
                            methodSymbol.Locations.FirstOrDefault(),
                            targetMethodName,
                            targetTypeSymbol.Name));
                    }
                    else
                    {
                        var targetNs = targetTypeSymbol.ContainingNamespace.IsGlobalNamespace
                            ? ""
                            : targetTypeSymbol.ContainingNamespace.ToDisplayString();

                        var targetModel = new InvalidationTargetModel(
                            targetTypeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                            targetTypeSymbol.Name,
                            targetNs,
                            targetMethodName,
                            template,
                            new EquatableArray<string>(tagsList));

                        invalidationTargets.Add(targetModel);
                        pendingInvalidations.Add((targetModel, targetTypeSymbol));
                    }
                }
            }

            methods.Add(new CacheMethodModel(
                methodSymbol.Name,
                returnTypeStr,
                isValueTask,
                template,
                policyName,
                localTtl,
                distributedTtl,
                new EquatableArray<string>(tagsList),
                new EquatableArray<ParameterModel>(parameters),
                new EquatableArray<InvalidationTargetModel>(invalidationTargets)));
        }

        var interfaceModel = new CacheInterfaceModel(
            ns,
            interfaceName,
            new EquatableArray<CacheMethodModel>(methods));

        return new InterfaceExtractionResult(
            interfaceModel,
            new EquatableArray<DiagnosticInfo>(diagnostics),
            new EquatableArray<(InvalidationTargetModel, INamedTypeSymbol)>(pendingInvalidations));
    }

    private static DecoratorInterfaceModel BuildDecoratorModel(
        INamedTypeSymbol targetInterfaceSymbol,
        List<InvalidationTargetModel> invalidations)
    {
        var ns = targetInterfaceSymbol.ContainingNamespace.IsGlobalNamespace
            ? ""
            : targetInterfaceSymbol.ContainingNamespace.ToDisplayString();
        var interfaceName = targetInterfaceSymbol.Name;
        var fullName = targetInterfaceSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        var cleanName = interfaceName.StartsWith("I") && interfaceName.Length > 1
            ? interfaceName.Substring(1)
            : interfaceName;
        var decoratorClassName = $"{cleanName}CacheDecorator";

        var methods = new List<DecoratorMethodModel>();

        foreach (var member in targetInterfaceSymbol.GetMembers())
        {
            if (member is not IMethodSymbol method) continue;

            // Extract method signature
            var returnTypeStr = method.ReturnType.ToDisplayString(NullableFullyQualifiedFormat);
            var returnsVoid = method.ReturnsVoid;
            var isAsync = false;
            var isGenericTask = false;
            string? genericReturnType = null;

            if (method.ReturnType is INamedTypeSymbol namedReturn)
            {
                if (namedReturn.Name == "Task" || namedReturn.Name == "ValueTask")
                {
                    isAsync = true;
                    if (namedReturn.IsGenericType && namedReturn.TypeArguments.Length > 0)
                    {
                        isGenericTask = true;
                        genericReturnType = namedReturn.TypeArguments[0].ToDisplayString(NullableFullyQualifiedFormat);
                    }
                }
            }

            var parameters = new List<ParameterModel>();
            foreach (var p in method.Parameters)
            {
                var isCt = p.Type.ToDisplayString().Contains("CancellationToken");
                parameters.Add(new ParameterModel(
                    p.Name,
                    p.Type.ToDisplayString(NullableFullyQualifiedFormat),
                    isCt));
            }

            // Find matching invalidations for this method
            var matchingInvalidations = invalidations.Where(inv =>
                string.Equals(inv.MethodName, method.Name, StringComparison.Ordinal)).ToList();

            var evictionActions = new List<EvictionActionModel>();
            foreach (var inv in matchingInvalidations)
            {
                evictionActions.Add(new EvictionActionModel(inv.KeyTemplate, inv.TagTemplates));
            }

            // Check direct [InvalidatesTag] attributes on this method
            var directTagAttrs = method.GetAttributes().Where(a =>
                a.AttributeClass != null &&
                (a.AttributeClass.Name == "InvalidatesTagAttribute" ||
                 a.AttributeClass.Name == "InvalidatesTag")).ToList();

            if (directTagAttrs.Count > 0)
            {
                var directTags = new List<string>();
                foreach (var attr in directTagAttrs)
                {
                    if (attr.ConstructorArguments.Length > 0 &&
                        attr.ConstructorArguments[0].Value is string tag &&
                        !string.IsNullOrWhiteSpace(tag))
                    {
                        directTags.Add(tag);
                    }
                }
                if (directTags.Count > 0)
                {
                    evictionActions.Add(new EvictionActionModel(string.Empty, new EquatableArray<string>(directTags)));
                }
            }

            methods.Add(new DecoratorMethodModel(
                method.Name,
                returnTypeStr,
                returnsVoid,
                isAsync,
                isGenericTask,
                genericReturnType,
                new EquatableArray<ParameterModel>(parameters),
                new EquatableArray<EvictionActionModel>(evictionActions)));
        }

        return new DecoratorInterfaceModel(
            ns,
            interfaceName,
            fullName,
            decoratorClassName,
            new EquatableArray<DecoratorMethodModel>(methods));
    }

    private sealed class InterfaceExtractionResultComparer : IEqualityComparer<InterfaceExtractionResult>
    {
        public static readonly InterfaceExtractionResultComparer Instance = new();

        public bool Equals(InterfaceExtractionResult x, InterfaceExtractionResult y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;
            return Equals(x.InterfaceModel, y.InterfaceModel) &&
                   x.Diagnostics.Equals(y.Diagnostics);
        }

        public int GetHashCode(InterfaceExtractionResult obj)
        {
            return (obj.InterfaceModel?.GetHashCode() ?? 0) ^ obj.Diagnostics.GetHashCode();
        }
    }
}

public sealed class InterfaceExtractionResult(
    CacheInterfaceModel? interfaceModel,
    EquatableArray<DiagnosticInfo> diagnostics,
    EquatableArray<(InvalidationTargetModel, INamedTypeSymbol)> pendingInvalidations)
{
    public CacheInterfaceModel? InterfaceModel { get; } = interfaceModel;
    public EquatableArray<DiagnosticInfo> Diagnostics { get; } = diagnostics;
    public EquatableArray<(InvalidationTargetModel Target, INamedTypeSymbol InterfaceSymbol)> PendingInvalidations { get; } = pendingInvalidations;
}
