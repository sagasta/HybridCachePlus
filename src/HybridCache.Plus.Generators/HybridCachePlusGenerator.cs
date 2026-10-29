using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
            var unique = new List<InterfaceExtractionResult>();
            foreach (var r in pair.Left.Concat(pair.Right))
            {
                if (r.InterfaceModel != null &&
                    seen.Add($"{r.InterfaceModel.InterfaceNamespace}.{r.InterfaceModel.InterfaceName}"))
                {
                    unique.Add(r);
                }
            }
            return new EquatableArray<InterfaceExtractionResult>(unique);
        });

        // 2. Output diagnostics, extension methods, and decorators
        context.RegisterSourceOutput(allResults, static (spc, results) =>
        {
            var reported = new HashSet<DiagnosticInfo>();
            var decoratorShapes = new Dictionary<string, DecoratorInterfaceModel>();
            var evictionsByTarget = new Dictionary<string, List<PendingEvictionModel>>();

            foreach (var result in results)
            {
                foreach (var diag in result.Diagnostics)
                {
                    // Several contracts may point at the same mutating interface and report the same issue
                    if (reported.Add(diag))
                    {
                        spc.ReportDiagnostic(diag.ToDiagnostic());
                    }
                }

                if (result.InterfaceModel != null)
                {
                    var source = ExtensionMethodsEmitter.Emit(result.InterfaceModel);
                    var hintName = $"{result.InterfaceModel.InterfaceName}HybridCacheExtensions.g.cs";
                    spc.AddSource(hintName, SourceText.From(source, Encoding.UTF8));
                }

                foreach (var shape in result.DecoratorTargets)
                {
                    if (!decoratorShapes.ContainsKey(shape.InterfaceFullName))
                    {
                        decoratorShapes[shape.InterfaceFullName] = shape;
                    }
                }

                foreach (var eviction in result.PendingEvictions)
                {
                    if (!evictionsByTarget.TryGetValue(eviction.InterfaceFullName, out var list))
                    {
                        list = [];
                        evictionsByTarget[eviction.InterfaceFullName] = list;
                    }
                    list.Add(eviction);
                }
            }

            // Generate decorator for each target interface
            foreach (var shape in decoratorShapes.Values)
            {
                evictionsByTarget.TryGetValue(shape.InterfaceFullName, out var pending);
                var decoratorModel = MergeEvictions(shape, pending);

                // Emit Decorator Class
                var decoratorSource = DecoratorEmitter.Emit(decoratorModel);
                var decoratorHintName = $"{decoratorModel.DecoratorClassName}.g.cs";
                spc.AddSource(decoratorHintName, SourceText.From(decoratorSource, Encoding.UTF8));

                // Emit DI Registration
                var diSource = DiRegistrationEmitter.Emit(decoratorModel);
                var diHintName = $"{decoratorModel.DecoratorClassName}Extensions.g.cs";
                spc.AddSource(diHintName, SourceText.From(diSource, Encoding.UTF8));
            }
        });
    }

    private static DecoratorInterfaceModel MergeEvictions(DecoratorInterfaceModel shape, List<PendingEvictionModel>? pending)
    {
        if (pending == null || pending.Count == 0) return shape;

        var methods = shape.Methods.Select(method =>
        {
            var contributed = pending
                .Where(p => p.SignatureId == method.SignatureId)
                .Select(p => p.Action)
                .ToList();

            if (contributed.Count == 0) return method;

            // Contract evictions first, then the method's own [InvalidatesTag] evictions
            return new DecoratorMethodModel(
                method.SignatureId,
                method.MethodName,
                method.ReturnType,
                method.ReturnsVoid,
                method.IsAsync,
                method.IsGenericTask,
                method.GenericReturnType,
                method.Parameters,
                new EquatableArray<EvictionActionModel>(contributed.Concat(method.EvictionActions)),
                method.TypeParameterList,
                method.ConstraintClauses,
                method.ExplicitInterface);
        });

        return new DecoratorInterfaceModel(
            shape.InterfaceNamespace,
            shape.InterfaceName,
            shape.InterfaceFullName,
            shape.DecoratorClassName,
            new EquatableArray<DecoratorMethodModel>(methods),
            shape.PassThroughMembers);
    }

    private static InterfaceExtractionResult ExtractInterface(GeneratorAttributeSyntaxContext ctx)
    {
        var diagnostics = new List<DiagnosticInfo>();
        var decoratorTargets = new List<DecoratorInterfaceModel>();
        var pendingEvictions = new List<PendingEvictionModel>();
        var seenTargets = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        if (ctx.TargetSymbol is not INamedTypeSymbol interfaceSymbol || interfaceSymbol.TypeKind != TypeKind.Interface)
        {
            return new InterfaceExtractionResult(
                null,
                EquatableArray<DiagnosticInfo>.Empty,
                EquatableArray<DecoratorInterfaceModel>.Empty,
                EquatableArray<PendingEvictionModel>.Empty);
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
                if (!TryResolvePlaceholder(placeholder, methodSymbol.Parameters, out _))
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
                    if (!TryResolvePlaceholder(placeholder, methodSymbol.Parameters, out _))
                    {
                        diagnostics.Add(new DiagnosticInfo(
                            DiagnosticDescriptors.MissingTemplateParameter,
                            methodSymbol.Locations.FirstOrDefault(),
                            placeholder,
                            methodSymbol.Name));
                    }
                }
            }

            // Resolve Placeholders in Template and Tags (e.g. {tenantId} -> {query.TenantId} if query is an object)
            var resolvedTemplate = TemplateParser.ReplacePlaceholders(template, ph =>
                TryResolvePlaceholder(ph, methodSymbol.Parameters, out var res) ? res : null);

            var resolvedTagsList = tagsList.Select(tag =>
                TemplateParser.ReplacePlaceholders(tag, ph =>
                    TryResolvePlaceholder(ph, methodSymbol.Parameters, out var res) ? res : null)).ToList();

            // Validate Multi-Tenant Placeholder Isolation (HCP004)
            var hasTenantParam = methodSymbol.Parameters.Any(p =>
                string.Equals(p.Name, "tenantId", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.Name, "tenant", StringComparison.OrdinalIgnoreCase) ||
                (!IsSpecialOrPrimitiveType(p.Type) && GetPublicProperties(p.Type).Any(pr =>
                    string.Equals(pr.Name, "tenantId", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(pr.Name, "tenant", StringComparison.OrdinalIgnoreCase))));

            if (hasTenantParam)
            {
                var hasTenantPlaceholder = templatePlaceholders.Any(ph =>
                    ph.IndexOf("tenant", StringComparison.OrdinalIgnoreCase) >= 0);

                if (!hasTenantPlaceholder)
                {
                    diagnostics.Add(new DiagnosticInfo(
                        DiagnosticDescriptors.TenantPlaceholderMissingInTemplate,
                        methodSymbol.Locations.FirstOrDefault(),
                        methodSymbol.Name,
                        "tenantId",
                        template));
                }
            }

            var tenantExpression = BuildTenantExpression(methodSymbol, templatePlaceholders);

            // Extract InvalidatedBy Attributes (both generic [InvalidatedBy<T>] and non-generic [InvalidatedBy(typeof(T))])
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
                var targetFullName = targetTypeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

                foreach (var targetMethodName in methodNames)
                {
                    var targetOverloads = GetInterfaceMembers(targetTypeSymbol)
                        .Select(m => m.Member)
                        .OfType<IMethodSymbol>()
                        .Where(m => m.Name == targetMethodName && m.MethodKind == MethodKind.Ordinary)
                        .ToList();

                    if (!isInterface || targetOverloads.Count == 0)
                    {
                        diagnostics.Add(new DiagnosticInfo(
                            DiagnosticDescriptors.InvalidatedByTargetNotFound,
                            methodSymbol.Locations.FirstOrDefault(),
                            targetMethodName,
                            targetTypeSymbol.Name));
                        continue;
                    }

                    if (seenTargets.Add(targetTypeSymbol))
                    {
                        decoratorTargets.Add(BuildDecoratorShape(targetTypeSymbol, diagnostics));
                    }

                    // Resolve this contract's raw key/tag templates against every overload of the mutating method
                    foreach (var targetMethod in targetOverloads)
                    {
                        var action = BuildEvictionAction(template, tagsList, targetMethod, methodSymbol.Name, diagnostics);
                        if (action != null)
                        {
                            pendingEvictions.Add(new PendingEvictionModel(targetFullName, GetSignatureId(targetMethod), action));
                        }
                    }
                }
            }

            methods.Add(new CacheMethodModel(
                methodSymbol.Name,
                returnTypeStr,
                isValueTask,
                resolvedTemplate,
                policyName,
                tenantExpression,
                localTtl,
                distributedTtl,
                new EquatableArray<string>(resolvedTagsList),
                new EquatableArray<ParameterModel>(parameters)));
        }

        var interfaceModel = new CacheInterfaceModel(
            ns,
            interfaceName,
            new EquatableArray<CacheMethodModel>(methods));

        return new InterfaceExtractionResult(
            interfaceModel,
            new EquatableArray<DiagnosticInfo>(diagnostics),
            new EquatableArray<DecoratorInterfaceModel>(decoratorTargets),
            new EquatableArray<PendingEvictionModel>(pendingEvictions));
    }

    private static string GetSignatureId(IMethodSymbol method) =>
        method.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

    /// <summary>
    /// Re-resolves a contract's raw key/tag templates against the mutating method's own parameters.
    /// Anything that cannot be resolved is reported (HCP005) and skipped instead of emitting uncompilable code.
    /// </summary>
    private static EvictionActionModel? BuildEvictionAction(
        string keyTemplate,
        List<string> tagTemplates,
        IMethodSymbol targetMethod,
        string cacheMethodName,
        List<DiagnosticInfo> diagnostics)
    {
        var resolvedKey = string.Empty;
        if (TryResolveTemplate(keyTemplate, targetMethod.Parameters, out var keyExpr, out var missingKey))
        {
            resolvedKey = keyExpr;
        }
        else
        {
            ReportUnresolvedPlaceholders(missingKey, cacheMethodName, targetMethod, diagnostics);
        }

        var resolvedTags = new List<string>();
        foreach (var tag in tagTemplates)
        {
            if (TryResolveTemplate(tag, targetMethod.Parameters, out var tagExpr, out var missingTag))
            {
                resolvedTags.Add(tagExpr);
            }
            else
            {
                ReportUnresolvedPlaceholders(missingTag, cacheMethodName, targetMethod, diagnostics);
            }
        }

        return resolvedKey.Length > 0 || resolvedTags.Count > 0
            ? new EvictionActionModel(resolvedKey, new EquatableArray<string>(resolvedTags))
            : null;
    }

    private static void ReportUnresolvedPlaceholders(
        List<string> missingPlaceholders,
        string cacheMethodName,
        IMethodSymbol targetMethod,
        List<DiagnosticInfo> diagnostics)
    {
        foreach (var placeholder in missingPlaceholders)
        {
            diagnostics.Add(new DiagnosticInfo(
                DiagnosticDescriptors.UnresolvedInvalidationPlaceholder,
                targetMethod.Locations.FirstOrDefault(),
                placeholder,
                cacheMethodName,
                targetMethod.Name));
        }
    }

    /// <summary>
    /// Captures the mutating interface's shape (all methods, plus their own [InvalidatesTag] evictions)
    /// as a symbol-free model; contract evictions are merged in later by <see cref="MergeEvictions"/>.
    /// </summary>
    private static DecoratorInterfaceModel BuildDecoratorShape(
        INamedTypeSymbol targetInterfaceSymbol,
        List<DiagnosticInfo> diagnostics)
    {
        var ns = targetInterfaceSymbol.ContainingNamespace.IsGlobalNamespace
            ? ""
            : targetInterfaceSymbol.ContainingNamespace.ToDisplayString();
        var interfaceName = targetInterfaceSymbol.Name;
        var fullName = targetInterfaceSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        var decoratorClassName = $"{EmitterHelpers.StripInterfacePrefix(interfaceName)}CacheDecorator";

        var methods = new List<DecoratorMethodModel>();
        var passThroughMembers = new List<string>();

        foreach (var (member, declaringInterface) in GetInterfaceMembers(targetInterfaceSymbol))
        {
            // Inherited members are implemented explicitly: avoids clashes between base interfaces
            string? explicitInterface = SymbolEqualityComparer.Default.Equals(declaringInterface, targetInterfaceSymbol)
                ? null
                : declaringInterface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

            if (member is IPropertySymbol property)
            {
                passThroughMembers.Add(RenderPropertyPassThrough(property, explicitInterface));
                continue;
            }

            if (member is IEventSymbol eventSymbol)
            {
                passThroughMembers.Add(RenderEventPassThrough(eventSymbol, explicitInterface));
                continue;
            }

            if (member is not IMethodSymbol { MethodKind: MethodKind.Ordinary } method) continue;

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

            var parameters = method.Parameters.Select(ToParameterModel).ToList();

            var evictionActions = new List<EvictionActionModel>();

            // Check direct [InvalidatesTag] attributes on this method
            var directTags = new List<string>();
            foreach (var attr in method.GetAttributes())
            {
                if (attr.AttributeClass is { Name: "InvalidatesTagAttribute" or "InvalidatesTag" } &&
                    attr.ConstructorArguments.Length > 0 &&
                    attr.ConstructorArguments[0].Value is string tag &&
                    !string.IsNullOrWhiteSpace(tag))
                {
                    if (TryResolveTemplate(tag, method.Parameters, out var tagExpr, out var missingTag))
                    {
                        directTags.Add(tagExpr);
                    }
                    else
                    {
                        ReportUnresolvedPlaceholders(missingTag, "[InvalidatesTag]", method, diagnostics);
                    }
                }
            }
            if (directTags.Count > 0)
            {
                evictionActions.Add(new EvictionActionModel(string.Empty, new EquatableArray<string>(directTags)));
            }

            methods.Add(new DecoratorMethodModel(
                GetSignatureId(method),
                method.Name,
                returnTypeStr,
                returnsVoid,
                isAsync,
                isGenericTask,
                genericReturnType,
                new EquatableArray<ParameterModel>(parameters),
                new EquatableArray<EvictionActionModel>(evictionActions),
                method.TypeParameters.Length > 0 ? $"<{string.Join(", ", method.TypeParameters.Select(t => t.Name))}>" : "",
                BuildConstraintClauses(method, explicitInterface != null),
                explicitInterface));
        }

        return new DecoratorInterfaceModel(
            ns,
            interfaceName,
            fullName,
            decoratorClassName,
            new EquatableArray<DecoratorMethodModel>(methods),
            new EquatableArray<string>(passThroughMembers));
    }

    /// <summary>
    /// Returns the interface's own members followed by those inherited from its base interfaces,
    /// skipping static members and members that already have a default implementation.
    /// </summary>
    private static IEnumerable<(ISymbol Member, INamedTypeSymbol DeclaringInterface)> GetInterfaceMembers(INamedTypeSymbol interfaceSymbol)
    {
        foreach (var type in new[] { interfaceSymbol }.Concat(interfaceSymbol.AllInterfaces))
        {
            foreach (var member in type.GetMembers())
            {
                if (member.IsStatic || !member.IsAbstract) continue;
                yield return (member, type);
            }
        }
    }

    private static ParameterModel ToParameterModel(IParameterSymbol p)
    {
        var refModifier = p.RefKind switch
        {
            RefKind.Ref => "ref ",
            RefKind.Out => "out ",
            RefKind.In => "in ",
            RefKind.RefReadOnlyParameter => "ref readonly ",
            _ => ""
        };
        var argumentModifier = p.RefKind switch
        {
            RefKind.Ref or RefKind.RefReadOnlyParameter => "ref ",
            RefKind.Out => "out ",
            RefKind.In => "in ",
            _ => ""
        };
        var declarationModifier = (p.ScopedKind == ScopedKind.ScopedRef ? "scoped " : "") +
                                  (p.IsParams ? "params " : "") +
                                  refModifier;

        return new ParameterModel(
            EscapeIdentifier(p.Name),
            p.Type.ToDisplayString(NullableFullyQualifiedFormat),
            p.Type.ToDisplayString().Contains("CancellationToken"),
            declarationModifier,
            argumentModifier);
    }

    private static string EscapeIdentifier(string name) =>
        SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

    /// <summary>
    /// Implicit implementations must restate every constraint; explicit ones may only restate class/struct.
    /// </summary>
    private static string BuildConstraintClauses(IMethodSymbol method, bool isExplicit)
    {
        var sb = new StringBuilder();
        foreach (var tp in method.TypeParameters)
        {
            var constraints = new List<string>();
            if (tp.HasReferenceTypeConstraint) constraints.Add("class");
            else if (tp.HasUnmanagedTypeConstraint && !isExplicit) constraints.Add("unmanaged");
            else if (tp.HasValueTypeConstraint) constraints.Add("struct");

            if (!isExplicit)
            {
                if (tp.HasNotNullConstraint) constraints.Add("notnull");
                constraints.AddRange(tp.ConstraintTypes.Select(t => t.ToDisplayString(NullableFullyQualifiedFormat)));
                if (tp.HasConstructorConstraint && !tp.HasValueTypeConstraint) constraints.Add("new()");
            }

            if (constraints.Count > 0)
            {
                sb.Append($" where {tp.Name} : {string.Join(", ", constraints)}");
            }
        }
        return sb.ToString();
    }

    private static string RenderPropertyPassThrough(IPropertySymbol property, string? explicitInterface)
    {
        var type = property.Type.ToDisplayString(NullableFullyQualifiedFormat);
        var refKind = property.ReturnsByRefReadonly ? "ref readonly " : property.ReturnsByRef ? "ref " : "";
        var target = explicitInterface != null ? $"(({explicitInterface})_inner)" : "_inner";

        string name;
        string access;
        if (property.IsIndexer)
        {
            var parameters = property.Parameters.Select(ToParameterModel).ToList();
            name = $"this[{string.Join(", ", parameters.Select(p => p.Declaration))}]";
            access = $"{target}[{string.Join(", ", parameters.Select(p => p.Argument))}]";
        }
        else
        {
            name = EscapeIdentifier(property.Name);
            access = $"{target}.{name}";
        }

        var accessors = new List<string>();
        if (property.GetMethod != null) accessors.Add($"get => {(refKind.Length > 0 ? "ref " : "")}{access};");
        if (property.SetMethod != null) accessors.Add(property.SetMethod.IsInitOnly ? $"init => {access} = value;" : $"set => {access} = value;");

        var declaration = explicitInterface != null
            ? $"{refKind}{type} {explicitInterface}.{name}"
            : $"public {refKind}{type} {name}";
        return $"{declaration} {{ {string.Join(" ", accessors)} }}";
    }

    private static string RenderEventPassThrough(IEventSymbol eventSymbol, string? explicitInterface)
    {
        var type = eventSymbol.Type.ToDisplayString(NullableFullyQualifiedFormat);
        var target = explicitInterface != null ? $"(({explicitInterface})_inner)" : "_inner";
        var name = EscapeIdentifier(eventSymbol.Name);
        var declaration = explicitInterface != null
            ? $"event {type} {explicitInterface}.{name}"
            : $"public event {type} {name}";
        return $"{declaration} {{ add => {target}.{name} += value; remove => {target}.{name} -= value; }}";
    }

    private static bool TryResolveTemplate(
        string template,
        ImmutableArray<IParameterSymbol> methodParameters,
        out string resolvedTemplate,
        out List<string> unresolvedPlaceholders)
    {
        var missing = new List<string>();
        resolvedTemplate = TemplateParser.ReplacePlaceholders(template, ph =>
        {
            if (TryResolvePlaceholder(ph, methodParameters, out var res)) return res;
            missing.Add(ph);
            return null;
        });
        unresolvedPlaceholders = missing;
        return missing.Count == 0;
    }

    /// <summary>
    /// Builds a C# expression yielding the tenant identifier as <c>string?</c> for policy resolution and diagnostics.
    /// Non-string tenant identifiers (int, Guid, ...) are converted with the invariant culture.
    /// </summary>
    private static string BuildTenantExpression(IMethodSymbol methodSymbol, List<string> templatePlaceholders)
    {
        var tenantParam = methodSymbol.Parameters.FirstOrDefault(p =>
            string.Equals(p.Name, "tenantId", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.Name, "tenant", StringComparison.OrdinalIgnoreCase));

        if (tenantParam != null)
        {
            return ToTenantString(tenantParam.Name, tenantParam.Type);
        }

        foreach (var placeholder in templatePlaceholders)
        {
            if (placeholder.IndexOf("tenant", StringComparison.OrdinalIgnoreCase) >= 0 &&
                TryResolvePlaceholder(placeholder, methodSymbol.Parameters, out var expression, out var type) &&
                type != null)
            {
                return ToTenantString(expression, type);
            }
        }

        return "null";

        static string ToTenantString(string expression, ITypeSymbol type) =>
            type.SpecialType == SpecialType.System_String
                ? expression
                : $"global::System.Convert.ToString((object?)({expression}), global::System.Globalization.CultureInfo.InvariantCulture)";
    }

    private static bool TryResolvePlaceholder(
        string placeholder,
        ImmutableArray<IParameterSymbol> methodParameters,
        out string resolvedExpression)
    {
        return TryResolvePlaceholder(placeholder, methodParameters, out resolvedExpression, out _);
    }

    private static bool TryResolvePlaceholder(
        string placeholder,
        ImmutableArray<IParameterSymbol> methodParameters,
        out string resolvedExpression,
        out ITypeSymbol? resolvedType)
    {
        resolvedExpression = placeholder;
        resolvedType = null;

        // 1. If placeholder has a dot, e.g. "query.TenantId" or "command.Product.Id"
        if (placeholder.Contains('.'))
        {
            var parts = placeholder.Split('.');
            var rootName = parts[0];
            var param = methodParameters.FirstOrDefault(p =>
                string.Equals(p.Name, rootName, StringComparison.OrdinalIgnoreCase));

            if (param != null)
            {
                ITypeSymbol currentType = param.Type;
                var valid = true;
                for (int i = 1; i < parts.Length; i++)
                {
                    var propName = parts[i];
                    var prop = GetPublicProperties(currentType)
                        .FirstOrDefault(p => string.Equals(p.Name, propName, StringComparison.OrdinalIgnoreCase));

                    if (prop == null)
                    {
                        valid = false;
                        break;
                    }
                    currentType = prop.Type;
                }

                if (valid)
                {
                    resolvedType = currentType;
                    resolvedExpression = $"{param.Name}.{string.Join(".", parts.Skip(1))}";
                    return true;
                }
            }

            // If rootName didn't match a parameter, search if any object parameter has the leaf property
            var leafPropName = parts[parts.Length - 1];
            foreach (var p in methodParameters)
            {
                if (IsSpecialOrPrimitiveType(p.Type)) continue;

                var prop = GetPublicProperties(p.Type)
                    .FirstOrDefault(pr => string.Equals(pr.Name, leafPropName, StringComparison.OrdinalIgnoreCase));

                if (prop != null)
                {
                    resolvedType = prop.Type;
                    resolvedExpression = $"{p.Name}.{prop.Name}";
                    return true;
                }
            }

            return false;
        }

        // 2. Direct parameter match, e.g. "tenantId"
        var directParam = methodParameters.FirstOrDefault(p =>
            string.Equals(p.Name, placeholder, StringComparison.OrdinalIgnoreCase));

        if (directParam != null)
        {
            resolvedType = directParam.Type;
            resolvedExpression = directParam.Name;
            return true;
        }

        // 3. Search inside complex object parameters (e.g. query, command, dto)
        foreach (var p in methodParameters)
        {
            if (IsSpecialOrPrimitiveType(p.Type)) continue;

            var prop = GetPublicProperties(p.Type)
                .FirstOrDefault(pr => string.Equals(pr.Name, placeholder, StringComparison.OrdinalIgnoreCase));

            if (prop != null)
            {
                resolvedType = prop.Type;
                resolvedExpression = $"{p.Name}.{prop.Name}";
                return true;
            }

            // Check common Id aliases (e.g. placeholder is "productId" and prop is "Id")
            if (placeholder.EndsWith("id", StringComparison.OrdinalIgnoreCase) && placeholder.Length > 2)
            {
                var idProp = GetPublicProperties(p.Type)
                    .FirstOrDefault(pr => string.Equals(pr.Name, "Id", StringComparison.OrdinalIgnoreCase));

                if (idProp != null)
                {
                    resolvedType = idProp.Type;
                    resolvedExpression = $"{p.Name}.{idProp.Name}";
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsSpecialOrPrimitiveType(ITypeSymbol type)
    {
        if (type.ToDisplayString().Contains("CancellationToken")) return true;
        if (type.SpecialType != SpecialType.None) return true;
        var display = type.ToDisplayString();
        if (display is "System.Guid" or "System.DateTime" or "System.DateTimeOffset" or "System.TimeSpan" or "System.Decimal") return true;
        return false;
    }

    private static IEnumerable<IPropertySymbol> GetPublicProperties(ITypeSymbol type)
    {
        var current = type;
        while (current != null && current.SpecialType != SpecialType.System_Object)
        {
            foreach (var member in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (member.DeclaredAccessibility == Accessibility.Public && !member.IsStatic)
                {
                    yield return member;
                }
            }
            current = current.BaseType;
        }
    }
}
