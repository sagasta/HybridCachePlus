using Microsoft.CodeAnalysis;
using Xunit;

namespace HybridCache.Plus.Tests.Generator;

public class GeneratorUnitTests
{
    [Fact]
    public void Generator_ValidContract_EmitsExtensionAndDecoratorSourcesWithoutErrors()
    {
        const string source = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using HybridCache.Plus;

        namespace TestApp
        {
            public record ProductDto(string TenantId, long ProductId, string Name);

            public interface IProductRepository
            {
                Task<ProductDto> UpdateProductAsync(string tenantId, long productId, string name, CancellationToken cancellationToken = default);
            }

            [HybridCacheKeys]
            public partial interface ICatalogCache
            {
                [CacheTemplate("tenants:{tenantId}:products:{productId}",
                    LocalTtlSeconds = 60,
                    DistributedTtlSeconds = 600,
                    Tags = new[] { "tenant:{tenantId}" })]
                [InvalidatedBy(typeof(IProductRepository), nameof(IProductRepository.UpdateProductAsync))]
                ValueTask<ProductDto> GetProductAsync(string tenantId, long productId);
            }
        }
        """;

        var (diagnostics, sources) = GeneratorTestHelper.RunGenerator(source);

        var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.Empty(errors);
        Assert.NotEmpty(sources);

        // Verify that the extension methods were generated
        var extensionSource = sources.FirstOrDefault(s => s.HintName.Contains("CatalogCacheHybridCacheExtensions"));
        Assert.NotNull(extensionSource.SourceText);
        var extensionCode = extensionSource.SourceText.ToString();
        Assert.Contains("public static async ValueTask<global::TestApp.ProductDto> GetProductAsync", extensionCode);
        Assert.Contains("public static async ValueTask EvictProductAsync", extensionCode);
        Assert.Contains("public static async ValueTask EvictProductByTenantIdTagAsync", extensionCode);

        // Verify that the decorator and DI registration were generated
        var decoratorSource = sources.FirstOrDefault(s => s.HintName.Contains("ProductRepositoryCacheDecorator.g.cs"));
        Assert.NotNull(decoratorSource.SourceText);
        var decoratorCode = decoratorSource.SourceText.ToString();
        Assert.Contains("public sealed class ProductRepositoryCacheDecorator : global::TestApp.IProductRepository", decoratorCode);
        Assert.Contains("await _cache.RemoveAsync($\"tenants:{tenantId}:products:{productId}\"", decoratorCode);
        Assert.Contains("await _cache.RemoveByTagAsync($\"tenant:{tenantId}\"", decoratorCode);
    }

    [Fact]
    public void Generator_MissingTemplateParameter_EmitsHCP001()
    {
        const string source = """
        using System.Threading.Tasks;
        using HybridCache.Plus;

        namespace TestApp
        {
            [HybridCacheKeys]
            public partial interface ICatalogCache
            {
                // Notice: productId placeholder is in the template but NOT in the parameters!
                [CacheTemplate("tenants:{tenantId}:products:{productId}")]
                ValueTask<string> GetProductAsync(string tenantId);
            }
        }
        """;

        var (diagnostics, _) = GeneratorTestHelper.RunGenerator(source);

        var hcp001 = diagnostics.FirstOrDefault(d => d.Id == "HCP001");
        Assert.NotNull(hcp001);
        Assert.Contains("productId", hcp001.GetMessage());
    }

    [Fact]
    public void Generator_InvalidTargetMethodInInvalidatedBy_EmitsHCP002()
    {
        const string source = """
        using System.Threading.Tasks;
        using HybridCache.Plus;

        namespace TestApp
        {
            public interface IProductRepository
            {
                Task DoSomethingElseAsync();
            }

            [HybridCacheKeys]
            public partial interface ICatalogCache
            {
                // NonExistentMethod does not exist on IProductRepository
                [CacheTemplate("products:{id}")]
                [InvalidatedBy(typeof(IProductRepository), "NonExistentMethod")]
                ValueTask<string> GetProductAsync(long id);
            }
        }
        """;

        var (diagnostics, _) = GeneratorTestHelper.RunGenerator(source);

        var hcp002 = diagnostics.FirstOrDefault(d => d.Id == "HCP002");
        Assert.NotNull(hcp002);
        Assert.Contains("NonExistentMethod", hcp002.GetMessage());
    }

    [Fact]
    public void Generator_InvalidReturnType_EmitsHCP003()
    {
        const string source = """
        using HybridCache.Plus;

        namespace TestApp
        {
            [HybridCacheKeys]
            public partial interface ICatalogCache
            {
                // Sync string return type instead of ValueTask<T> or Task<T>
                [CacheTemplate("products:{id}")]
                string GetProduct(long id);
            }
        }
        """;

        var (diagnostics, _) = GeneratorTestHelper.RunGenerator(source);

        var hcp003 = diagnostics.FirstOrDefault(d => d.Id == "HCP003");
        Assert.NotNull(hcp003);
    }

    [Fact]
    public void Generator_GenericInvalidatedByWithMultipleMethods_EmitsDecoratorCorrectly()
    {
        const string source = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using HybridCache.Plus;

        namespace TestApp
        {
            public record ProductDto(string TenantId, long ProductId, string Name);

            public interface IProductRepository
            {
                Task<ProductDto> UpdateProductAsync(string tenantId, long productId, string name, CancellationToken cancellationToken = default);
                Task DeleteProductAsync(string tenantId, long productId, CancellationToken cancellationToken = default);
            }

            [HybridCacheKeys]
            public partial interface ICatalogCache
            {
                [CacheTemplate("tenants:{tenantId}:products:{productId}")]
                [InvalidatedBy<IProductRepository>(
                    nameof(IProductRepository.UpdateProductAsync),
                    nameof(IProductRepository.DeleteProductAsync))]
                ValueTask<ProductDto> GetProductAsync(string tenantId, long productId);
            }
        }
        """;

        var (diagnostics, sources) = GeneratorTestHelper.RunGenerator(source);

        var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.Empty(errors);

        var decoratorSource = sources.FirstOrDefault(s => s.HintName.Contains("ProductRepositoryCacheDecorator.g.cs"));
        Assert.NotNull(decoratorSource.SourceText);
        var code = decoratorSource.SourceText.ToString();
        Assert.Contains("UpdateProductAsync", code);
        Assert.Contains("DeleteProductAsync", code);
        Assert.Contains("await _cache.RemoveAsync($\"tenants:{tenantId}:products:{productId}\"", code);
    }

    [Fact]
    public void Generator_TenantParameterWithoutTenantPlaceholderInTemplate_EmitsHCP004()
    {
        const string source = """
        using System;
        using System.Threading.Tasks;
        using HybridCache.Plus;

        namespace TestApp
        {
            public record ProductDto(string Name);

            [HybridCacheKeys]
            public partial interface ICatalogCache
            {
                // Has tenantId parameter but key template only has products:{productId}, risking cross-tenant L1 collision
                [CacheTemplate("products:{productId}")]
                ValueTask<ProductDto> GetProductAsync(string tenantId, long productId);
            }
        }
        """;

        var (diagnostics, _) = GeneratorTestHelper.RunGenerator(source);

        var hcp004 = diagnostics.FirstOrDefault(d => d.Id == "HCP004");
        Assert.NotNull(hcp004);
        Assert.Equal(DiagnosticSeverity.Warning, hcp004.Severity);
    }

    [Fact]
    public void Generator_CustomPolicyName_EmitsPolicyNameInRegistryResolve()
    {
        const string source = """
        using System;
        using System.Threading.Tasks;
        using HybridCache.Plus;

        namespace TestApp
        {
            [HybridCacheKeys]
            public partial interface IOrderCache
            {
                [CacheTemplate("orders:{orderId}", PolicyName = "CustomOrderPolicy", LocalTtlSeconds = 30)]
                ValueTask<string> GetOrderAsync(long orderId);
            }
        }
        """;

        var (diagnostics, sources) = GeneratorTestHelper.RunGenerator(source);
        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));

        var extensionSource = sources.FirstOrDefault(s => s.HintName.Contains("OrderCacheHybridCacheExtensions"));
        Assert.NotNull(extensionSource.SourceText);
        var code = extensionSource.SourceText.ToString();

        Assert.Contains("HybridCachePlusPolicyRegistry.Resolve(\"CustomOrderPolicy\"", code);
    }
}
