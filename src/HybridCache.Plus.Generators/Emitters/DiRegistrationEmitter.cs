using System.Text;
using HybridCache.Plus.Generators.Models;

namespace HybridCache.Plus.Generators.Emitters;

public static class DiRegistrationEmitter
{
    public static string Emit(DecoratorInterfaceModel model)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine("using Microsoft.Extensions.Caching.Hybrid;");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(model.InterfaceNamespace))
        {
            sb.AppendLine($"namespace {model.InterfaceNamespace}");
            sb.AppendLine("{");
        }

        var indent = string.IsNullOrEmpty(model.InterfaceNamespace) ? "" : "    ";
        var cleanInterfaceName = model.InterfaceName.StartsWith("I") && model.InterfaceName.Length > 1
            ? model.InterfaceName.Substring(1)
            : model.InterfaceName;

        var extensionsClassName = $"{cleanInterfaceName}CacheDecoratorExtensions";

        sb.AppendLine($"{indent}/// <summary>");
        sb.AppendLine($"{indent}/// Dependency injection registration extensions for <see cref=\"{model.DecoratorClassName}\"/>.");
        sb.AppendLine($"{indent}/// </summary>");
        sb.AppendLine($"{indent}public static class {extensionsClassName}");
        sb.AppendLine($"{indent}{{");

        // Method 1: Decorate existing registration
        sb.AppendLine($"{indent}    /// <summary>");
        sb.AppendLine($"{indent}    /// Decorates the registered <see cref=\"{model.InterfaceFullName}\"/> with the caching decorator <see cref=\"{model.DecoratorClassName}\"/>.");
        sb.AppendLine($"{indent}    /// </summary>");
        sb.AppendLine($"{indent}    public static IServiceCollection Decorate{cleanInterfaceName}WithCache(this IServiceCollection services)");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        for (int i = services.Count - 1; i >= 0; i--)");
        sb.AppendLine($"{indent}        {{");
        sb.AppendLine($"{indent}            var descriptor = services[i];");
        sb.AppendLine($"{indent}            if (descriptor.ServiceType == typeof({model.InterfaceFullName}))");
        sb.AppendLine($"{indent}            {{");
        sb.AppendLine($"{indent}                if (descriptor.ImplementationType != null)");
        sb.AppendLine($"{indent}                {{");
        sb.AppendLine($"{indent}                    var implType = descriptor.ImplementationType;");
        sb.AppendLine($"{indent}                    services[i] = ServiceDescriptor.Describe(");
        sb.AppendLine($"{indent}                        typeof({model.InterfaceFullName}),");
        sb.AppendLine($"{indent}                        sp =>");
        sb.AppendLine($"{indent}                        {{");
        sb.AppendLine($"{indent}                            var inner = ({model.InterfaceFullName})ActivatorUtilities.GetServiceOrCreateInstance(sp, implType);");
        sb.AppendLine($"{indent}                            var cache = sp.GetRequiredService<global::Microsoft.Extensions.Caching.Hybrid.HybridCache>();");
        sb.AppendLine($"{indent}                            var publisher = sp.GetService<global::HybridCache.Plus.Backplane.IEvictionPublisher>();");
        sb.AppendLine($"{indent}                            return new {model.DecoratorClassName}(inner, cache, publisher);");
        sb.AppendLine($"{indent}                        }},");
        sb.AppendLine($"{indent}                        descriptor.Lifetime);");
        sb.AppendLine($"{indent}                }}");
        sb.AppendLine($"{indent}                else if (descriptor.ImplementationFactory != null)");
        sb.AppendLine($"{indent}                {{");
        sb.AppendLine($"{indent}                    var factory = descriptor.ImplementationFactory;");
        sb.AppendLine($"{indent}                    services[i] = ServiceDescriptor.Describe(");
        sb.AppendLine($"{indent}                        typeof({model.InterfaceFullName}),");
        sb.AppendLine($"{indent}                        sp =>");
        sb.AppendLine($"{indent}                        {{");
        sb.AppendLine($"{indent}                            var inner = ({model.InterfaceFullName})factory(sp);");
        sb.AppendLine($"{indent}                            var cache = sp.GetRequiredService<global::Microsoft.Extensions.Caching.Hybrid.HybridCache>();");
        sb.AppendLine($"{indent}                            var publisher = sp.GetService<global::HybridCache.Plus.Backplane.IEvictionPublisher>();");
        sb.AppendLine($"{indent}                            return new {model.DecoratorClassName}(inner, cache, publisher);");
        sb.AppendLine($"{indent}                        }},");
        sb.AppendLine($"{indent}                        descriptor.Lifetime);");
        sb.AppendLine($"{indent}                }}");
        sb.AppendLine($"{indent}                else if (descriptor.ImplementationInstance != null)");
        sb.AppendLine($"{indent}                {{");
        sb.AppendLine($"{indent}                    var instance = ({model.InterfaceFullName})descriptor.ImplementationInstance;");
        sb.AppendLine($"{indent}                    services[i] = ServiceDescriptor.Describe(");
        sb.AppendLine($"{indent}                        typeof({model.InterfaceFullName}),");
        sb.AppendLine($"{indent}                        sp =>");
        sb.AppendLine($"{indent}                        {{");
        sb.AppendLine($"{indent}                            var cache = sp.GetRequiredService<global::Microsoft.Extensions.Caching.Hybrid.HybridCache>();");
        sb.AppendLine($"{indent}                            var publisher = sp.GetService<global::HybridCache.Plus.Backplane.IEvictionPublisher>();");
        sb.AppendLine($"{indent}                            return new {model.DecoratorClassName}(instance, cache, publisher);");
        sb.AppendLine($"{indent}                        }},");
        sb.AppendLine($"{indent}                        descriptor.Lifetime);");
        sb.AppendLine($"{indent}                }}");
        sb.AppendLine($"{indent}                break;");
        sb.AppendLine($"{indent}            }}");
        sb.AppendLine($"{indent}        }}");
        sb.AppendLine($"{indent}        return services;");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine();

        // Method 2: Register directly with implementation type
        sb.AppendLine($"{indent}    /// <summary>");
        sb.AppendLine($"{indent}    /// Registers <typeparamref name=\"TImplementation\"/> as <see cref=\"{model.InterfaceFullName}\"/> wrapped in <see cref=\"{model.DecoratorClassName}\"/>.");
        sb.AppendLine($"{indent}    /// </summary>");
        sb.AppendLine($"{indent}    public static IServiceCollection Add{cleanInterfaceName}CacheDecorator<TImplementation>(");
        sb.AppendLine($"{indent}        this IServiceCollection services,");
        sb.AppendLine($"{indent}        ServiceLifetime lifetime = ServiceLifetime.Scoped)");
        sb.AppendLine($"{indent}        where TImplementation : class, {model.InterfaceFullName}");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        services.Add(new ServiceDescriptor(typeof(TImplementation), typeof(TImplementation), lifetime));");
        sb.AppendLine($"{indent}        services.Add(new ServiceDescriptor(");
        sb.AppendLine($"{indent}            typeof({model.InterfaceFullName}),");
        sb.AppendLine($"{indent}            sp =>");
        sb.AppendLine($"{indent}            {{");
        sb.AppendLine($"{indent}                var inner = sp.GetRequiredService<TImplementation>();");
        sb.AppendLine($"{indent}                var cache = sp.GetRequiredService<global::Microsoft.Extensions.Caching.Hybrid.HybridCache>();");
        sb.AppendLine($"{indent}                var publisher = sp.GetService<global::HybridCache.Plus.Backplane.IEvictionPublisher>();");
        sb.AppendLine($"{indent}                return new {model.DecoratorClassName}(inner, cache, publisher);");
        sb.AppendLine($"{indent}            }},");
        sb.AppendLine($"{indent}            lifetime));");
        sb.AppendLine($"{indent}        return services;");
        sb.AppendLine($"{indent}    }}");

        sb.AppendLine($"{indent}}}");

        if (!string.IsNullOrEmpty(model.InterfaceNamespace))
        {
            sb.AppendLine("}");
        }

        return sb.ToString();
    }
}
