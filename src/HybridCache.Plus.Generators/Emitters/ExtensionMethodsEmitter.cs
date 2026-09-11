using System.Text;
using HybridCache.Plus.Generators.Models;
using HybridCache.Plus.Generators.Parsers;

namespace HybridCache.Plus.Generators.Emitters;

public static class ExtensionMethodsEmitter
{
    public static string Emit(CacheInterfaceModel model)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Threading;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine("using Microsoft.Extensions.Caching.Hybrid;");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(model.InterfaceNamespace))
        {
            sb.AppendLine($"namespace {model.InterfaceNamespace}");
            sb.AppendLine("{");
        }

        var className = $"{model.InterfaceName.TrimStart('I')}HybridCacheExtensions";

        sb.AppendLine($"    public static partial class {className}");
        sb.AppendLine("    {");

        foreach (var method in model.Methods)
        {
            EmitMethodExtensions(sb, method);
        }

        sb.AppendLine("    }");

        if (!string.IsNullOrEmpty(model.InterfaceNamespace))
        {
            sb.AppendLine("}");
        }

        return sb.ToString();
    }

    private static void EmitMethodExtensions(StringBuilder sb, CacheMethodModel method)
    {
        var paramSignature = string.Join(", ", method.Parameters.Select(p => $"{p.Type} {p.Name}"));
        var formatKeyMethodName = $"Format{GetBaseMethodName(method.MethodName)}Key";

        // 1. Static Key Formatter Method
        sb.AppendLine($"        /// <summary>");
        sb.AppendLine($"        /// Formats the cache key for <see cref=\"{method.MethodName}\"/> without runtime allocations.");
        sb.AppendLine($"        /// </summary>");
        sb.AppendLine($"        public static string {formatKeyMethodName}({paramSignature})");
        sb.AppendLine("        {");
        sb.AppendLine($"            return $\"{method.KeyTemplate}\";");
        sb.AppendLine("        }");
        sb.AppendLine();

        // 2. Pre-calculated EntryOptions
        var optionsFieldName = $"_options_{method.MethodName}";
        sb.AppendLine($"        private static readonly HybridCacheEntryOptions {optionsFieldName} = new()");
        sb.AppendLine("        {");
        if (method.LocalTtlSeconds > 0)
        {
            sb.AppendLine($"            LocalCacheExpiration = TimeSpan.FromSeconds({method.LocalTtlSeconds}),");
        }
        if (method.DistributedTtlSeconds > 0)
        {
            sb.AppendLine($"            Expiration = TimeSpan.FromSeconds({method.DistributedTtlSeconds}),");
        }
        sb.AppendLine("        };");
        sb.AppendLine();

        // 3. GetOrCreateAsync (Factory with CancellationToken)
        var getMethodName = method.MethodName;
        var extraParams = method.Parameters.Count > 0
            ? ", " + string.Join(", ", method.Parameters.Select(p => $"{p.Type} {p.Name}"))
            : "";

        var tenantParam = method.Parameters.FirstOrDefault(p =>
            string.Equals(p.Name, "tenantId", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.Name, "tenant", StringComparison.OrdinalIgnoreCase));
        var tenantArg = tenantParam != null ? tenantParam.Name : "null";

        sb.AppendLine($"        /// <summary>");
        sb.AppendLine($"        /// Retrieves or creates an entry for {method.MethodName} in HybridCache using compiled typed keys and configurable policy TTLs.");
        sb.AppendLine($"        /// </summary>");
        sb.AppendLine($"        public static async ValueTask<{method.ReturnType}> {getMethodName}(");
        sb.AppendLine($"            this global::Microsoft.Extensions.Caching.Hybrid.HybridCache cache{extraParams},");
        sb.AppendLine($"            Func<CancellationToken, ValueTask<{method.ReturnType}>> factory,");
        sb.AppendLine($"            HybridCacheEntryOptions? options = null,");
        sb.AppendLine($"            IEnumerable<string>? tags = null,");
        sb.AppendLine($"            CancellationToken cancellationToken = default)");
        sb.AppendLine("        {");
        sb.AppendLine($"            string cacheKey = {formatKeyMethodName}({string.Join(", ", method.Parameters.Select(p => p.Name))});");
        sb.AppendLine($"            options ??= global::HybridCache.Plus.Policies.HybridCachePlusPolicyRegistry.Resolve(\"{method.PolicyName}\", {tenantArg}, {optionsFieldName});");

        if (method.Tags.Count > 0)
        {
            var tagInterpolations = method.Tags.Select(t => $"$\"{t}\"");
            sb.AppendLine($"            tags ??= new string[] {{ {string.Join(", ", tagInterpolations)} }};");
        }

        sb.AppendLine($"            if (global::HybridCache.Plus.Diagnostics.HybridCachePlusDiagnostics.IsMetricsEnabled)");
        sb.AppendLine($"            {{");
        sb.AppendLine($"                var stopwatch = global::System.Diagnostics.Stopwatch.StartNew();");
        sb.AppendLine($"                using var activity = global::HybridCache.Plus.Diagnostics.HybridCachePlusDiagnostics.StartActivity(\"{method.MethodName}\", \"{method.PolicyName}\", {tenantArg});");
        sb.AppendLine($"                bool isMiss = false;");
        sb.AppendLine($"                var result = await cache.GetOrCreateAsync(");
        sb.AppendLine($"                    cacheKey,");
        sb.AppendLine($"                    async ct =>");
        sb.AppendLine($"                    {{");
        sb.AppendLine($"                        isMiss = true;");
        sb.AppendLine($"                        return await factory(ct).ConfigureAwait(false);");
        sb.AppendLine($"                    }},");
        sb.AppendLine($"                    options,");
        sb.AppendLine($"                    tags,");
        sb.AppendLine($"                    cancellationToken).ConfigureAwait(false);");
        sb.AppendLine($"                stopwatch.Stop();");
        sb.AppendLine($"                global::HybridCache.Plus.Diagnostics.HybridCachePlusDiagnostics.RecordDuration(stopwatch.Elapsed.TotalMilliseconds, \"GetOrCreate\", \"{method.PolicyName}\", {tenantArg});");
        sb.AppendLine($"                if (isMiss)");
        sb.AppendLine($"                {{");
        sb.AppendLine($"                    global::HybridCache.Plus.Diagnostics.HybridCachePlusDiagnostics.RecordMiss(\"{method.PolicyName}\", {tenantArg}, \"{method.KeyTemplate}\");");
        sb.AppendLine($"                }}");
        sb.AppendLine($"                else");
        sb.AppendLine($"                {{");
        sb.AppendLine($"                    global::HybridCache.Plus.Diagnostics.HybridCachePlusDiagnostics.RecordHit(\"{method.PolicyName}\", {tenantArg}, \"{method.KeyTemplate}\");");
        sb.AppendLine($"                }}");
        sb.AppendLine($"                return result;");
        sb.AppendLine($"            }}");
        sb.AppendLine();
        sb.AppendLine($"            return await cache.GetOrCreateAsync(");
        sb.AppendLine($"                cacheKey,");
        sb.AppendLine($"                factory,");
        sb.AppendLine($"                options,");
        sb.AppendLine($"                tags,");
        sb.AppendLine($"                cancellationToken).ConfigureAwait(false);");
        sb.AppendLine("        }");
        sb.AppendLine();

        // 4. GetOrCreateAsync with State (Zero-Allocation)
        sb.AppendLine($"        /// <summary>");
        sb.AppendLine($"        /// State-based zero-allocation overload for {method.MethodName}.");
        sb.AppendLine($"        /// </summary>");
        sb.AppendLine($"        public static async ValueTask<{method.ReturnType}> {getMethodName}<TState>(");
        sb.AppendLine($"            this global::Microsoft.Extensions.Caching.Hybrid.HybridCache cache{extraParams},");
        sb.AppendLine($"            TState state,");
        sb.AppendLine($"            Func<TState, CancellationToken, ValueTask<{method.ReturnType}>> factory,");
        sb.AppendLine($"            global::Microsoft.Extensions.Caching.Hybrid.HybridCacheEntryOptions? options = null,");
        sb.AppendLine($"            IEnumerable<string>? tags = null,");
        sb.AppendLine($"            CancellationToken cancellationToken = default)");
        sb.AppendLine("        {");
        sb.AppendLine($"            string cacheKey = {formatKeyMethodName}({string.Join(", ", method.Parameters.Select(p => p.Name))});");
        sb.AppendLine($"            options ??= global::HybridCache.Plus.Policies.HybridCachePlusPolicyRegistry.Resolve(\"{method.PolicyName}\", {tenantArg}, {optionsFieldName});");

        if (method.Tags.Count > 0)
        {
            var tagInterpolations = method.Tags.Select(t => $"$\"{t}\"");
            sb.AppendLine($"            tags ??= new string[] {{ {string.Join(", ", tagInterpolations)} }};");
        }

        sb.AppendLine($"            if (global::HybridCache.Plus.Diagnostics.HybridCachePlusDiagnostics.IsMetricsEnabled)");
        sb.AppendLine($"            {{");
        sb.AppendLine($"                var stopwatch = global::System.Diagnostics.Stopwatch.StartNew();");
        sb.AppendLine($"                using var activity = global::HybridCache.Plus.Diagnostics.HybridCachePlusDiagnostics.StartActivity(\"{method.MethodName}\", \"{method.PolicyName}\", {tenantArg});");
        sb.AppendLine($"                bool isMiss = false;");
        sb.AppendLine($"                var result = await cache.GetOrCreateAsync(");
        sb.AppendLine($"                    cacheKey,");
        sb.AppendLine($"                    (state, factory),");
        sb.AppendLine($"                    async (s, ct) =>");
        sb.AppendLine($"                    {{");
        sb.AppendLine($"                        isMiss = true;");
        sb.AppendLine($"                        return await s.factory(s.state, ct).ConfigureAwait(false);");
        sb.AppendLine($"                    }},");
        sb.AppendLine($"                    options,");
        sb.AppendLine($"                    tags,");
        sb.AppendLine($"                    cancellationToken).ConfigureAwait(false);");
        sb.AppendLine($"                stopwatch.Stop();");
        sb.AppendLine($"                global::HybridCache.Plus.Diagnostics.HybridCachePlusDiagnostics.RecordDuration(stopwatch.Elapsed.TotalMilliseconds, \"GetOrCreate\", \"{method.PolicyName}\", {tenantArg});");
        sb.AppendLine($"                if (isMiss)");
        sb.AppendLine($"                {{");
        sb.AppendLine($"                    global::HybridCache.Plus.Diagnostics.HybridCachePlusDiagnostics.RecordMiss(\"{method.PolicyName}\", {tenantArg}, \"{method.KeyTemplate}\");");
        sb.AppendLine($"                }}");
        sb.AppendLine($"                else");
        sb.AppendLine($"                {{");
        sb.AppendLine($"                    global::HybridCache.Plus.Diagnostics.HybridCachePlusDiagnostics.RecordHit(\"{method.PolicyName}\", {tenantArg}, \"{method.KeyTemplate}\");");
        sb.AppendLine($"                }}");
        sb.AppendLine($"                return result;");
        sb.AppendLine($"            }}");
        sb.AppendLine();
        sb.AppendLine($"            return await cache.GetOrCreateAsync(");
        sb.AppendLine($"                cacheKey,");
        sb.AppendLine($"                state,");
        sb.AppendLine($"                factory,");
        sb.AppendLine($"                options,");
        sb.AppendLine($"                tags,");
        sb.AppendLine($"                cancellationToken).ConfigureAwait(false);");
        sb.AppendLine("        }");
        sb.AppendLine();

        // 5. EvictKeyAsync
        var evictMethodName = GetEvictMethodName(method.MethodName);
        sb.AppendLine($"        /// <summary>");
        sb.AppendLine($"        /// Evicts the cached key for {method.MethodName} and broadcasts to distributed backplane if active.");
        sb.AppendLine($"        /// </summary>");
        sb.AppendLine($"        public static async ValueTask {evictMethodName}(");
        sb.AppendLine($"            this global::Microsoft.Extensions.Caching.Hybrid.HybridCache cache{extraParams},");
        sb.AppendLine($"            CancellationToken cancellationToken = default)");
        sb.AppendLine("        {");
        sb.AppendLine($"            string cacheKey = {formatKeyMethodName}({string.Join(", ", method.Parameters.Select(p => p.Name))});");
        sb.AppendLine($"            await cache.RemoveAsync(cacheKey, cancellationToken).ConfigureAwait(false);");
        sb.AppendLine($"            global::HybridCache.Plus.Diagnostics.HybridCachePlusDiagnostics.RecordEviction(cacheKey, \"ManualEvict\", {tenantArg});");
        sb.AppendLine($"            var publisher = global::HybridCache.Plus.Backplane.HybridCachePlusBackplaneContext.GetPublisher(cache);");
        sb.AppendLine($"            if (publisher != null)");
        sb.AppendLine($"            {{");
        sb.AppendLine($"                await publisher.PublishAsync(global::HybridCache.Plus.Backplane.BackplaneEvictionMessage.CreateKey(cacheKey), cancellationToken).ConfigureAwait(false);");
        sb.AppendLine($"            }}");
        sb.AppendLine("        }");
        sb.AppendLine();

        // 6. Evict by Tag methods
        foreach (var tag in method.Tags)
        {
            var placeholders = TemplateParser.ExtractPlaceholders(tag);
            var tagParams = method.Parameters.Where(p => placeholders.Contains(p.Name, StringComparer.OrdinalIgnoreCase)).ToList();

            var tagMethodSuffix = BuildTagMethodSuffix(tag, placeholders);
            var tagEvictMethodName = $"Evict{GetBaseMethodName(method.MethodName)}By{tagMethodSuffix}TagAsync";
            var tagParamSig = tagParams.Count > 0
                ? ", " + string.Join(", ", tagParams.Select(p => $"{p.Type} {p.Name}"))
                : "";

            sb.AppendLine($"        /// <summary>");
            sb.AppendLine($"        /// Evicts all cached entries matching tag '{tag}' and broadcasts to distributed backplane if active.");
            sb.AppendLine($"        /// </summary>");
            sb.AppendLine($"        public static async ValueTask {tagEvictMethodName}(");
            sb.AppendLine($"            this global::Microsoft.Extensions.Caching.Hybrid.HybridCache cache{tagParamSig},");
            sb.AppendLine($"            CancellationToken cancellationToken = default)");
            sb.AppendLine("        {");
            sb.AppendLine($"            string tag = $\"{tag}\";");
            sb.AppendLine($"            await cache.RemoveByTagAsync(tag, cancellationToken).ConfigureAwait(false);");
            sb.AppendLine($"            global::HybridCache.Plus.Diagnostics.HybridCachePlusDiagnostics.RecordEviction(tag, \"ManualTagEvict\");");
            sb.AppendLine($"            var publisher = global::HybridCache.Plus.Backplane.HybridCachePlusBackplaneContext.GetPublisher(cache);");
            sb.AppendLine($"            if (publisher != null)");
            sb.AppendLine($"            {{");
            sb.AppendLine($"                await publisher.PublishAsync(global::HybridCache.Plus.Backplane.BackplaneEvictionMessage.CreateTag(tag), cancellationToken).ConfigureAwait(false);");
            sb.AppendLine($"            }}");
            sb.AppendLine("        }");
            sb.AppendLine();
        }
    }

    private static string GetBaseMethodName(string methodName)
    {
        var name = methodName;
        if (name.EndsWith("Async", StringComparison.OrdinalIgnoreCase))
        {
            name = name.Substring(0, name.Length - 5);
        }
        if (name.StartsWith("Get", StringComparison.OrdinalIgnoreCase))
        {
            name = name.Substring(3);
        }
        return string.IsNullOrEmpty(name) ? methodName : name;
    }

    private static string GetEvictMethodName(string methodName)
    {
        var baseName = GetBaseMethodName(methodName);
        return $"Evict{baseName}Async";
    }

    private static string BuildTagMethodSuffix(string tag, List<string> placeholders)
    {
        if (placeholders.Count > 0)
        {
            return string.Join("And", placeholders.Select(CapitalizeFirstLetter));
        }

        var parts = tag.Split([':', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join("", parts.Select(CapitalizeFirstLetter));
    }

    private static string CapitalizeFirstLetter(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return char.ToUpperInvariant(s[0]) + s.Substring(1);
    }
}
