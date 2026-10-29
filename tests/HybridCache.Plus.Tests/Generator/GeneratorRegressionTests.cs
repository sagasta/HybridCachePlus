using Microsoft.CodeAnalysis;
using Xunit;

namespace HybridCache.Plus.Tests.Generator;

/// <summary>
/// Regression tests that compile the generated output, not just inspect it.
/// </summary>
public class GeneratorRegressionTests
{
    private static string FormatErrors(IEnumerable<Diagnostic> errors) =>
        string.Join(Environment.NewLine, errors.Select(e => e.ToString()));

    [Fact]
    public void Generator_ValidContract_GeneratedCodeCompiles()
    {
        const string source = """
        using System.Threading;
        using System.Threading.Tasks;
        using HybridCache.Plus;

        namespace TestApp
        {
            public record ProductDto(string TenantId, long ProductId, string Name);

            public interface IProductRepository
            {
                Task<ProductDto> UpdateProductAsync(string tenantId, long productId, string name, CancellationToken cancellationToken = default);
                void Touch(string tenantId, long productId);
            }

            [HybridCacheKeys]
            public partial interface ICatalogCache
            {
                [CacheTemplate("tenants:{tenantId}:products:{productId}", LocalTtlSeconds = 60, Tags = new[] { "tenant:{tenantId}" })]
                [InvalidatedBy(typeof(IProductRepository), nameof(IProductRepository.UpdateProductAsync), nameof(IProductRepository.Touch))]
                ValueTask<ProductDto> GetProductAsync(string tenantId, long productId);
            }
        }
        """;

        var (generatorDiagnostics, compilationErrors, _) = GeneratorTestHelper.RunGeneratorAndCompile(source);

        Assert.DoesNotContain(generatorDiagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.True(compilationErrors.IsEmpty, FormatErrors(compilationErrors));
    }

    [Fact]
    public void Generator_CacheMethodWithQueryObject_MutationWithFlatParameters_DecoratorCompiles()
    {
        // The cache method resolves {tenantId}/{productId} to query.TenantId/query.ProductId.
        // The decorator must re-resolve the ORIGINAL placeholders against the mutating method, not "query.*".
        const string source = """
        using System.Threading;
        using System.Threading.Tasks;
        using HybridCache.Plus;

        namespace TestApp
        {
            public record ProductQuery(string TenantId, long ProductId);

            public interface IProductRepository
            {
                Task UpdateProductAsync(string tenantId, long productId, CancellationToken cancellationToken = default);
            }

            [HybridCacheKeys]
            public partial interface ICatalogCache
            {
                [CacheTemplate("tenants:{tenantId}:products:{productId}", Tags = new[] { "tenant:{tenantId}" })]
                [InvalidatedBy(typeof(IProductRepository), nameof(IProductRepository.UpdateProductAsync))]
                ValueTask<string> GetProductAsync(ProductQuery query);
            }
        }
        """;

        var (generatorDiagnostics, compilationErrors, sources) = GeneratorTestHelper.RunGeneratorAndCompile(source);

        Assert.DoesNotContain(generatorDiagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.True(compilationErrors.IsEmpty, FormatErrors(compilationErrors));

        var decorator = sources.Single(s => s.HintName == "ProductRepositoryCacheDecorator.g.cs").SourceText.ToString();
        Assert.Contains("tenants:{tenantId}:products:{productId}", decorator);
        Assert.Contains("tenant:{tenantId}", decorator);
    }

    [Fact]
    public void Generator_UnresolvablePlaceholderInMutatingMethod_ReportsHCP005AndStillCompiles()
    {
        const string source = """
        using System.Threading;
        using System.Threading.Tasks;
        using HybridCache.Plus;

        namespace TestApp
        {
            public interface IProductRepository
            {
                // No productId available here
                Task RenameTenantAsync(string tenantId, CancellationToken cancellationToken = default);
            }

            [HybridCacheKeys]
            public partial interface ICatalogCache
            {
                [CacheTemplate("tenants:{tenantId}:products:{productId}", Tags = new[] { "tenant:{tenantId}" })]
                [InvalidatedBy(typeof(IProductRepository), nameof(IProductRepository.RenameTenantAsync))]
                ValueTask<string> GetProductAsync(string tenantId, long productId);
            }
        }
        """;

        var (generatorDiagnostics, compilationErrors, sources) = GeneratorTestHelper.RunGeneratorAndCompile(source);

        var hcp005 = Assert.Single(generatorDiagnostics, d => d.Id == "HCP005");
        Assert.Contains("productId", hcp005.GetMessage());
        Assert.True(compilationErrors.IsEmpty, FormatErrors(compilationErrors));

        // The resolvable tag eviction is still emitted; the unresolvable key eviction is not.
        var decorator = sources.Single(s => s.HintName == "ProductRepositoryCacheDecorator.g.cs").SourceText.ToString();
        Assert.Contains("RemoveByTagAsync", decorator);
        Assert.DoesNotContain("_cache.RemoveAsync(", decorator);
    }

    [Fact]
    public void Generator_NonStringTenantParameter_Compiles()
    {
        const string source = """
        using System;
        using System.Threading.Tasks;
        using HybridCache.Plus;

        namespace TestApp
        {
            public record OrderQuery(int TenantId, Guid OrderId);

            [HybridCacheKeys]
            public partial interface IOrderCache
            {
                [CacheTemplate("tenants:{tenantId}:orders:{orderId}")]
                ValueTask<string> GetOrderAsync(int tenantId, Guid orderId);

                [CacheTemplate("tenants:{query.TenantId}:orders:{query.OrderId}")]
                ValueTask<string> FindOrderAsync(OrderQuery query);
            }
        }
        """;

        var (generatorDiagnostics, compilationErrors, _) = GeneratorTestHelper.RunGeneratorAndCompile(source);

        Assert.DoesNotContain(generatorDiagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.True(compilationErrors.IsEmpty, FormatErrors(compilationErrors));
    }

    [Fact]
    public void Generator_InterfaceNameStartingWithDoubleI_KeepsSecondLetterInClassName()
    {
        const string source = """
        using System.Threading.Tasks;
        using HybridCache.Plus;

        namespace TestApp
        {
            [HybridCacheKeys]
            public partial interface IInventoryCache
            {
                [CacheTemplate("inventory:{sku}")]
                ValueTask<int> GetStockAsync(string sku);
            }
        }
        """;

        var (_, compilationErrors, sources) = GeneratorTestHelper.RunGeneratorAndCompile(source);

        Assert.True(compilationErrors.IsEmpty, FormatErrors(compilationErrors));
        var extensions = Assert.Single(sources).SourceText.ToString();
        Assert.Contains("public static partial class InventoryCacheHybridCacheExtensions", extensions);
    }

    [Fact]
    public void Generator_CacheKey_IsFormattedWithInvariantCulture()
    {
        const string source = """
        using System;
        using System.Threading.Tasks;
        using HybridCache.Plus;

        namespace TestApp
        {
            [HybridCacheKeys]
            public partial interface IPriceCache
            {
                [CacheTemplate("prices:{amount}:{day}")]
                ValueTask<string> GetPriceAsync(decimal amount, DateTime day);
            }
        }
        """;

        var (_, compilationErrors, sources) = GeneratorTestHelper.RunGeneratorAndCompile(source);

        Assert.True(compilationErrors.IsEmpty, FormatErrors(compilationErrors));
        var extensions = Assert.Single(sources).SourceText.ToString();
        Assert.Contains("string.Create(global::System.Globalization.CultureInfo.InvariantCulture, $\"prices:{amount}:{day}\")", extensions);
    }

    [Fact]
    public void Generator_UnrelatedEdit_ReusesCachedOutputs()
    {
        const string contracts = """
        using System.Threading;
        using System.Threading.Tasks;
        using HybridCache.Plus;

        namespace TestApp
        {
            public record ProductQuery(string TenantId, long ProductId);

            public interface IProductRepository
            {
                [InvalidatesTag("tenant:{tenantId}")]
                Task UpdateProductAsync(string tenantId, long productId, CancellationToken cancellationToken = default);
            }

            [HybridCacheKeys]
            public partial interface ICatalogCache
            {
                [CacheTemplate("tenants:{tenantId}:products:{productId}", Tags = new[] { "tenant:{tenantId}" })]
                [InvalidatedBy(typeof(IProductRepository), nameof(IProductRepository.UpdateProductAsync))]
                ValueTask<string> GetProductAsync(ProductQuery query);
            }
        }
        """;

        var trackedSteps = GeneratorTestHelper.RunTwiceWithUnrelatedEdit(contracts);

        Assert.NotEmpty(trackedSteps);
        Assert.All(trackedSteps, step => Assert.True(
            step.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged,
            $"Output step was re-executed ({step.Reason}) after an unrelated edit."));
    }

    [Fact]
    public void Generator_Diagnostics_PointToTheOffendingLine()
    {
        const string source = """
        using System.Threading.Tasks;
        using HybridCache.Plus;

        namespace TestApp
        {
            [HybridCacheKeys]
            public partial interface ICatalogCache
            {
                [CacheTemplate("products:{productId}")]
                ValueTask<string> GetProductAsync(string sku);
            }
        }
        """;

        var (generatorDiagnostics, _, _) = GeneratorTestHelper.RunGeneratorAndCompile(source);

        var hcp001 = Assert.Single(generatorDiagnostics, d => d.Id == "HCP001");
        // Line index 9 (0-based) is the GetProductAsync declaration.
        Assert.Equal(9, hcp001.Location.GetLineSpan().StartLinePosition.Line);
    }
}
