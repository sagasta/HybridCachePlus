using Microsoft.CodeAnalysis;
using Xunit;

namespace HybridCache.Plus.Tests.Generator;

/// <summary>
/// Compiles generated code for interface shapes commonly found in real repositories.
/// </summary>
public class GeneratorShapeTests
{
    private static void AssertCompiles(string source)
    {
        var (generatorDiagnostics, compilationErrors, _) = GeneratorTestHelper.RunGeneratorAndCompile(source);
        Assert.DoesNotContain(generatorDiagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.True(compilationErrors.IsEmpty, string.Join(Environment.NewLine, compilationErrors.Select(e => e.ToString())));
    }

    [Fact]
    public void Decorator_TargetInterfaceInheritingFromBaseInterface_Compiles()
    {
        AssertCompiles("""
        using System.Threading;
        using System.Threading.Tasks;
        using HybridCache.Plus;

        namespace TestApp
        {
            public interface IRepository<T>
            {
                Task<T?> GetAsync(long id, CancellationToken cancellationToken = default);
                Task DeleteAsync(long id, CancellationToken cancellationToken = default);
            }

            public interface IProductRepository : IRepository<string>
            {
                Task UpdateProductAsync(string tenantId, long productId, CancellationToken cancellationToken = default);
            }

            [HybridCacheKeys]
            public partial interface ICatalogCache
            {
                [CacheTemplate("tenants:{tenantId}:products:{productId}")]
                [InvalidatedBy(typeof(IProductRepository), nameof(IProductRepository.UpdateProductAsync))]
                ValueTask<string> GetProductAsync(string tenantId, long productId);
            }
        }
        """);
    }

    [Fact]
    public void Decorator_TargetInterfaceWithPropertyAndEvent_Compiles()
    {
        AssertCompiles("""
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using HybridCache.Plus;

        namespace TestApp
        {
            public interface IProductRepository
            {
                string Name { get; }
                event EventHandler? Changed;
                Task UpdateProductAsync(string tenantId, long productId, CancellationToken cancellationToken = default);
            }

            [HybridCacheKeys]
            public partial interface ICatalogCache
            {
                [CacheTemplate("tenants:{tenantId}:products:{productId}")]
                [InvalidatedBy(typeof(IProductRepository), nameof(IProductRepository.UpdateProductAsync))]
                ValueTask<string> GetProductAsync(string tenantId, long productId);
            }
        }
        """);
    }

    [Fact]
    public void Decorator_RefOutParamsAndGenericMethods_Compile()
    {
        AssertCompiles("""
        using System.Threading;
        using System.Threading.Tasks;
        using HybridCache.Plus;

        namespace TestApp
        {
            public interface IProductRepository
            {
                bool TryGet(long productId, out string? name);
                void Swap(ref long a, ref long b);
                int Sum(params int[] values);
                Task<T> ExecuteAsync<T>(string tenantId, T value) where T : class;
                Task UpdateProductAsync(string tenantId, long productId, CancellationToken cancellationToken = default);
            }

            [HybridCacheKeys]
            public partial interface ICatalogCache
            {
                [CacheTemplate("tenants:{tenantId}:products:{productId}")]
                [InvalidatedBy(typeof(IProductRepository), nameof(IProductRepository.UpdateProductAsync))]
                ValueTask<string> GetProductAsync(string tenantId, long productId);
            }
        }
        """);
    }

    [Fact]
    public void Decorator_ParameterNamedResult_Compiles()
    {
        AssertCompiles("""
        using System.Threading.Tasks;
        using HybridCache.Plus;

        namespace TestApp
        {
            public record Result(string TenantId, long ProductId);

            public interface IProductRepository
            {
                Task<int> SaveAsync(string tenantId, long productId, Result result);
            }

            [HybridCacheKeys]
            public partial interface ICatalogCache
            {
                [CacheTemplate("tenants:{tenantId}:products:{productId}")]
                [InvalidatedBy(typeof(IProductRepository), nameof(IProductRepository.SaveAsync))]
                ValueTask<string> GetProductAsync(string tenantId, long productId);
            }
        }
        """);
    }

    [Fact]
    public void CacheContract_OverloadedMethods_Compile()
    {
        AssertCompiles("""
        using System.Threading.Tasks;
        using HybridCache.Plus;

        namespace TestApp
        {
            public record ProductQuery(string TenantId, long ProductId);

            [HybridCacheKeys]
            public partial interface ICatalogCache
            {
                [CacheTemplate("tenants:{tenantId}:products:{productId}", LocalTtlSeconds = 30)]
                ValueTask<string> GetProductAsync(string tenantId, long productId);

                [CacheTemplate("tenants:{tenantId}:products:{productId}", LocalTtlSeconds = 60)]
                ValueTask<string> GetProductAsync(ProductQuery query);
            }
        }
        """);
    }
}
